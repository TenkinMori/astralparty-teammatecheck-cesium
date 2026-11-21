using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace TeammateCheckMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new TeammateCheckController());
        }
    }

    /// <summary>
    /// 对队友进行大调查（CesiumLoader 版）。
    ///
    /// 第一件事：解除名片的「隐藏」。
    /// 游戏里「隐藏资料 / 隐藏战绩」是账号上的两个开关（ShowPlayer.IsShowData、
    /// ShowPlayer.IsShowFight），由服务端保存；别人打开你的名片时，服务端仍然把
    /// 整份 ShowPlayerInfo（含统计与战绩列表）发给客户端，客户端在
    /// UI.AccountInfoWindow 里按 IsSelf || isShowX 决定要不要渲染，隐藏时只显示
    /// 一句「对方已隐藏」的提示。本模组不改游戏文件、也不伪造数据：只在打开别人的
    /// 名片时把 PlayerCareer.isShowData / isShowFight 临时置为 true 并重刷界面，
    /// 关掉后按原值还原。自己的名片（IsSelf）一律不碰。
    ///
    /// 第二件事：开局查「谁和谁常一起玩」。
    /// 5154（看名片）返回的 Record[] 只有名次/英雄，没有队友；同场玩家的 UID 在
    /// 5155（单场详情）的 PlayerFightData.PlayerId 里。所以对当前房间每个人各查
    /// 一次名片拿最近 N 局的 Index，再逐局查详情，按「同一局（Time+MapType）」把
    /// 参与者归并起来，最后两两统计同场次数：近 N 局里同场 ≥ minTogether 次即判
    /// 疑似组队，结果画在局内左下角的小浮窗里，同时写进日志。
    /// 查询走裸 RPC / AccountLogic.RequestGetPlayerFightRecordC2S，不会弹名片窗口；
    /// 每个玩家的结果缓存 30 分钟，换一局只补查新面孔。
    /// </summary>
    public sealed class TeammateCheckController : ModBase
    {
        private const double PollSeconds = 0.2;

        /// <summary>UIManager 里缓存的名片窗口；null 表示从没打开过。</summary>
        private static readonly FieldInfo WindowField =
            typeof(UIManager).GetField("_cachedAccountInfoWindow", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>名片窗口当前展示的 PlayerCareer。</summary>
        private static readonly FieldInfo CareerField =
            typeof(AccountInfoWindow).GetField("playerCareer", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>private void RefreshStatisticalData()：统计页渲染。</summary>
        private static readonly MethodInfo RefreshStatMethod =
            typeof(AccountInfoWindow).GetMethod("RefreshStatisticalData", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>private void RefreshFightRecord(List&lt;BattleShortRecord&gt;)：战绩页渲染。</summary>
        private static readonly MethodInfo RefreshFightMethod =
            typeof(AccountInfoWindow).GetMethod("RefreshFightRecord", BindingFlags.Instance | BindingFlags.NonPublic);

        private bool _enabled = true;
        private bool _showPanel = true;
        private bool _selectOnly = false;
        private int _minTogether = 3;
        private int _maxRecords = 20;
        private KeyCode _toggleKey = KeyCode.F9;
        private bool _toggleHeld;

        private double _nextPoll;
        private bool _warnedLayout;
        private bool _warnedNoData;

        private long _appliedPlayerId;
        private PlayerCareer _appliedCareer;
        private bool _savedShowData;
        private bool _savedShowFight;

        private TeamScanner _scanner;
        private ModPanels _panel;
        private string _panelState;
        private string _loggedSignature;

        public override string Name { get { return "对队友进行大调查"; } }
        public override string Version { get { return "1.2.3"; } }
        public override string Author { get { return "TenkinMori"; } }
        public override string Description
        {
            get { return "别人隐藏的资料和战绩照样摊开看；开局还会静默比对当前房间每个人的近期战绩，把疑似组队的人合并成一组列出。"; }
        }

        public override void OnInitialize()
        {
            ReadConfig(true);
            CreateScanner();

            Log.Info("配置: enabled=" + _enabled
                + "，minTogether=" + _minTogether
                + "，maxRecords=" + _maxRecords
                + "，showPanel=" + _showPanel
                + "，selectOnly=" + _selectOnly
                + "，toggleKey=" + _toggleKey);
            Log.Info("字段: 名片窗口=" + (WindowField != null)
                + "，名片数据=" + (CareerField != null)
                + "，统计刷新=" + (RefreshStatMethod != null)
                + "，战绩刷新=" + (RefreshFightMethod != null));

            if (WindowField == null || CareerField == null) WarnLayout();
        }

        public override void OnConfigChanged()
        {
            ReadConfig(false);
            CreateScanner();
            if (!_enabled) { Restore(); _panel.HideAll(); }
            Log.Info("配置已更新: enabled=" + _enabled
                + "，minTogether=" + _minTogether
                + "，maxRecords=" + _maxRecords
                + "，showPanel=" + _showPanel
                + "，selectOnly=" + _selectOnly
                + "，toggleKey=" + _toggleKey);
        }

        public override void OnUnload()
        {
            Restore();
            if (_panel != null) _panel.HideAll();
            if (_scanner != null) _scanner.ClearLobby();
        }

        public override void OnUpdate()
        {
            // 热键每帧查（不能放进 0.2s 节流里，否则按一下可能整段被跳过）。
            try { TickHotkey(); }
            catch (Exception e) { Log.Warn("热键检查失败: " + e.Message); }

            // 与 TeamScanner.Now 同一时间基准（DateTime.UtcNow.Ticks * 1e-7），
            // 不能再用 TimeOfDay：那个每天归零，跨午夜会把间隔算成负数。
            double now = DateTime.UtcNow.Ticks * 1e-7;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;

            try { TickCard(); }
            catch (Exception e) { Log.Warn("名片巡检失败: " + e.Message); }

            try { TickScan(now); }
            catch (Exception e) { Log.Warn("组队扫描失败: " + e.Message); }
        }

        /// <summary>
        /// 快捷键总开关：一次隐藏/显示所有自绘面板（疑似组队 + 成员列表）。
        /// 用 IsKeyHeld 自己算下降沿，不依赖 IsKeyPressed —— 后者等价 GetKeyDown，
        /// 掉一帧就丢（ExtremeDifficultyMod 踩过这个坑）。
        /// </summary>
        private void TickHotkey()
        {
            if (_panel == null) return;

            bool held = _toggleKey != KeyCode.None
                && InputService.IsAvailable
                && InputService.IsKeyHeld(_toggleKey);
            bool edge = held && !_toggleHeld;
            _toggleHeld = held;

            if (!edge) return;
            if (InputService.IsKeyboardCaptured) return;   // 正在打字就别抢键

            bool wanted = !_panel.Wanted;
            _panel.SetWanted(wanted);
            Log.Info(wanted
                ? "面板已显示（" + _toggleKey + " 再按一次隐藏）。"
                : "面板已隐藏（" + _toggleKey + " 再按一次显示）。");
        }

        // ---------------------------------------------------------------- 名片

        private void TickCard()
        {
            if (!_enabled) { Restore(); return; }
            if (WindowField == null || CareerField == null) return;

            AccountInfoWindow window;
            PlayerCareer career;
            if (!TryGetOpenCareer(out window, out career) || career.playerId == 0L)
            {
                Restore();
                return;
            }

            // 自己的名片不动：隐藏开关的语义要保持原样。
            if (IsSelf(career.playerId))
            {
                Restore();
                return;
            }

            if (_appliedCareer != null && career.playerId != _appliedPlayerId)
            {
                // 同一份 PlayerCareer 被新玩家的 GetShowPlayerS2C 覆盖了：旧的还原点作废，
                // 不能把上一个人的开关值写给下一个人。
                _appliedCareer = null;
                _appliedPlayerId = 0L;
            }

            if (_appliedPlayerId == career.playerId) return;   // 这张名片已经处理过

            bool hasStats = career.statistics != null;
            bool hasRecords = career.Records != null;
            if (!hasStats && !hasRecords)
            {
                if (!_warnedNoData)
                {
                    _warnedNoData = true;
                    Log.Info("名片：对方隐藏了资料/战绩，但服务端没有下发数据（statistics=null，Records=null），本地无从展开。");
                }
                return;
            }

            _warnedNoData = false;
            _appliedPlayerId = career.playerId;
            _appliedCareer = career;
            _savedShowData = career.isShowData;
            _savedShowFight = career.isShowFight;

            if (hasStats) career.isShowData = true;
            if (hasRecords) career.isShowFight = true;

            bool statOk = hasStats && RefreshStat(window);
            bool fightOk = hasRecords && RefreshFight(window, career);
            if (!statOk && !fightOk) WarnLayout();

            Log.Info("名片：已强制展开对方隐藏内容（统计=" + (hasStats ? "有" : "无")
                + "，战绩=" + (hasRecords ? career.Records.Count + " 条" : "无")
                + "，刷新=" + statOk + "/" + fightOk + "）");
        }

        /// <summary>窗口正在显示，且能取到它当前展示的 PlayerCareer。</summary>
        private bool TryGetOpenCareer(out AccountInfoWindow window, out PlayerCareer career)
        {
            window = null;
            career = null;
            try
            {
                if (!SimpleSingletonProvider<UIManager>.hasInstance) return false;

                UIManager ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui == null) return false;

                // 只读缓存，不碰 UIManager.AccountInfo（那个属性会懒构造窗口）。
                window = WindowField.GetValue(ui) as AccountInfoWindow;
                if (window == null || !window.isShowing) { window = null; return false; }

                career = CareerField.GetValue(window) as PlayerCareer;
                return career != null;
            }
            catch
            {
                window = null;
                career = null;
                return false;
            }
        }

        /// <summary>名片窗口是否正开着——开着时暂停扫描，免得两边抢 playerCareer。</summary>
        private bool CardWindowOpen()
        {
            try
            {
                if (WindowField == null || !SimpleSingletonProvider<UIManager>.hasInstance) return false;
                UIManager ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui == null) return false;
                AccountInfoWindow window = WindowField.GetValue(ui) as AccountInfoWindow;
                return window != null && window.isShowing;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSelf(long playerId)
        {
            try
            {
                GameLogicManager logic = SimpleSingletonProvider<GameLogicManager>.inst;
                if (logic == null || logic.account == null) return true;
                return logic.account.IsSelf(playerId);
            }
            catch
            {
                return true;    // 判断不了就当是自己，宁可不改
            }
        }

        /// <summary>名片关掉/换人/自己被查看时，把之前改过的两个开关还原。</summary>
        private void Restore()
        {
            PlayerCareer career = _appliedCareer;
            if (career == null) return;

            _appliedCareer = null;
            _appliedPlayerId = 0L;

            try
            {
                // 游戏已经用新的 GetShowPlayerS2C 覆盖过（两个标志都是 false）时不要往回写，
                // 否则会把别的玩家刚刷新的状态改错。
                if (!career.isShowData && !career.isShowFight) return;

                career.isShowData = _savedShowData;
                career.isShowFight = _savedShowFight;
                Log.Info("名片已关闭：隐藏开关已还原。");
            }
            catch (Exception e)
            {
                Log.Warn("还原名片开关失败: " + e.Message);
            }
        }

        private bool RefreshStat(AccountInfoWindow window)
        {
            if (RefreshStatMethod == null) return false;
            try
            {
                RefreshStatMethod.Invoke(window, null);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("刷新统计页失败: " + Unwrap(e).Message);
                return false;
            }
        }

        private bool RefreshFight(AccountInfoWindow window, PlayerCareer career)
        {
            if (RefreshFightMethod == null) return false;
            try
            {
                RefreshFightMethod.Invoke(window, new object[] { career.Records });
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("刷新战绩页失败: " + Unwrap(e).Message);
                return false;
            }
        }

        private static Exception Unwrap(Exception e)
        {
            return (e is TargetInvocationException && e.InnerException != null) ? e.InnerException : e;
        }

        // ------------------------------------------------------------ 组队扫描

        private void CreateScanner()
        {
            _scanner = new TeamScanner(_maxRecords, Log.Info, Log.Warn);
            _scanner.MinTogether = _minTogether;
            _panelState = null;
            _loggedSignature = null;

            if (_panel == null)
            {
                _panel = new ModPanels(Log.Warn, Log.Info, PickMember);
                _panel.SetMemberProvider(CollectMembers);
            }
            _panel.SetWanted(_showPanel);
        }

        private void TickScan(double now)
        {
            if (!_enabled)
            {
                _scanner.ClearLobby();
                _panel.HideAll();
                return;
            }

            RoomInfo room = GetRoom();
            if (room == null || room.Players == null || room.Players.Count == 0)
            {
                _scanner.ClearLobby();
                _panel.HideAll();
                return;
            }

            List<long> ids = new List<long>();
            List<string> names = new List<string>();
            for (int i = 0; i < room.Players.Count; i++)
            {
                RoomPlayer player = room.Players[i];
                if (player == null || player.serverPlayer == null) continue;
                if (player.IsBot) continue;
                if (player.Id <= 100000L) continue;      // NPC / 占位 id
                ids.Add(player.Id);
                names.Add(player.GetNick());
            }

            if (ids.Count < 2)
            {
                _scanner.ClearLobby();
                _panel.HidePairs();
                return;
            }

            _scanner.SetLobby(ids, names);
            _scanner.Tick(now, CardWindowOpen());

            // 名片窗口开着、或配置了「只在选人页面显示」而已经离开选人页面时先让开，条件不满足再回来。
            _panel.SetSuppressed(CardWindowOpen() || !IsPanelSceneAllowed());

            if (!_scanner.IsDirty) return;
            _scanner.MarkClean();
            PushPanel();
        }

        private static RoomInfo GetRoom()
        {
            try
            {
                GameLogicManager logic = SimpleSingletonProvider<GameLogicManager>.inst;
                if (logic == null || logic.room == null) return null;
                if (!logic.room.IsInRoom) return null;
                return logic.room.curRoomInfo;
            }
            catch
            {
                return null;
            }
        }

        private void PushPanel()
        {
            List<TeamGroup> groups = _scanner.Groups();
            List<string> lines = new List<string>();
            for (int i = 0; i < groups.Count && i < 8; i++)
            {
                lines.Add(JoinNames(groups[i]) + " 疑似组队");
            }

            string signature = string.Join("|", lines);
            bool allowed = IsPanelSceneAllowed();
            bool visible = _showPanel && allowed && lines.Count > 0;

            // 显隐状态也要参与去重：面板被藏起来之后，即便内容没变也得重新 Show 一次。
            string state = (visible ? "1|" : "0|") + signature;
            if (state != _panelState)
            {
                _panelState = state;
                if (visible) _panel.ShowPairs("疑似组队", lines);
                else _panel.HidePairs();
            }

            // 成员列表内容变了（换人 / 名单变）就同步刷新。
            List<MemberRow> members = CollectMembers();
            if (_panel.MembersOpen) _panel.ShowMembers(members);

            // 只在扫完、且结果确实变了时落日志，避免刷屏。
            if (_scanner.ScannedPlayers < _scanner.LobbyCount) return;
            if (signature == _loggedSignature) return;
            _loggedSignature = signature;
            for (int i = 0; i < lines.Count; i++) Log.Info(lines[i]);
        }

        /// <summary>把一组人拼成「A、B、C」，已剥掉 UBB 标记。</summary>
        private static string JoinNames(TeamGroup group)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < group.Names.Count; i++)
            {
                if (i > 0) sb.Append('、');
                sb.Append(StripTags(group.Names[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 配置了「只在选人页面显示」时，只有房间角色选择面板（RoomHero）才允许出面板；
        /// 扫描本身不受影响，结果照样进日志。
        /// </summary>
        private bool IsPanelSceneAllowed()
        {
            if (!_selectOnly) return true;

            try
            {
                if (!SimpleSingletonProvider<UIManager>.hasInstance) return false;
                UIManager ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui == null || ui.currentPanel == null || ui.currentPanel.config == null) return false;
                return ui.currentPanel.config.PanelType == UIPanelType.RoomHero;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>当前房间的真人名单（供成员列表面板用）。</summary>
        private List<MemberRow> CollectMembers()
        {
            List<MemberRow> rows = new List<MemberRow>();
            RoomInfo room = GetRoom();
            if (room == null || room.Players == null) return rows;

            for (int i = 0; i < room.Players.Count; i++)
            {
                RoomPlayer player = room.Players[i];
                if (player == null || player.serverPlayer == null) continue;
                if (player.IsBot) continue;
                if (player.Id <= 100000L) continue;

                MemberRow row = new MemberRow();
                row.Id = player.Id;
                row.Name = StripTags(player.GetNick());
                row.IsSelf = IsSelf(player.Id);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>点了成员列表里的一行：走游戏自己的看名片路径（会弹出名片窗口）。</summary>
        private void PickMember(long playerId)
        {
            if (playerId == 0L) return;

            RoomPlayer player = FindPlayer(playerId);
            if (player == null)
            {
                Log.Warn("房间成员已不在：id=" + playerId);
                return;
            }

            try
            {
                GameLogicManager logic = SimpleSingletonProvider<GameLogicManager>.inst;
                if (logic == null || logic.account == null) return;

                // 看自己时游戏约定传 0（UICom_RoomPlayer.ShowAccountInfo 的写法）。
                long id = IsSelf(playerId) ? 0L : playerId;
                string nick = player.GetNick();
                int level = player.Level;
                string headIcon = SafeHead(player);
                (string, bool) labelData = SafeBackground(player);

                logic.account.RequestGetShowPlayerC2S(id, nick, level, headIcon, labelData, null);
                Log.Info("正在查看名片：" + StripTags(nick));
            }
            catch (Exception e)
            {
                Log.Warn("打开名片失败: " + e.Message);
            }
        }

        private static RoomPlayer FindPlayer(long playerId)
        {
            RoomInfo room = GetRoom();
            if (room == null || room.Players == null) return null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                RoomPlayer player = room.Players[i];
                if (player != null && player.Id == playerId) return player;
            }
            return null;
        }

        private static string SafeHead(RoomPlayer player)
        {
            try { return player.HeadURL() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static (string, bool) SafeBackground(RoomPlayer player)
        {
            try { return player.AccountBackgroundURL(); }
            catch { return (string.Empty, false); }
        }

        /// <summary>房间昵称常带 UBB 标记（[size=54][color=#E75480]…），浮窗里要剥掉。</summary>
        private static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0) return text;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            bool inTag = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '[') { inTag = true; continue; }
                if (c == ']') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 配置

        private void ReadConfig(bool firstLoad)
        {
            ModConfig cfg = Config;
            if (cfg == null) return;

            // SetDefault 只补缺失的键，已有配置不会被覆盖；说明写进 _comments，供 Toys 配置界面显示。
            // 老配置（例如只有 {"enabled": true}）会在这次加载时补齐其余键。
            bool changed = false;
            changed |= cfg.SetDefault("enabled", true, ConfigDoc.Of(
                "是否启用模组。关闭后不扫战绩、不显示面板、也不解除名片隐藏。",
                "是否啟用模組。關閉後不掃戰績、不顯示面板，也不解除名片隱藏。",
                "Whether the mod is enabled. Disabling stops scanning, hides panels and leaves profiles untouched.",
                "モジュールを有効にするか。無効にすると戦績の取得・パネル表示・名刺の解除を行いません。"));
            changed |= cfg.SetDefault("toggleKey", "F9", ConfigDoc.Of(
                "隐藏 / 显示所有面板的按键，填 Unity KeyCode 名称：F9、F8、Insert、Home、BackQuote、KeyPad1 等。写 None 可停用热键。",
                "隱藏 / 顯示所有面板的按鍵，填 Unity KeyCode 名稱：F9、F8、Insert、Home、BackQuote、KeyPad1 等。寫 None 可停用熱鍵。",
                "Key that hides / shows all panels. Use a Unity KeyCode name: F9, F8, Insert, Home, BackQuote, KeyPad1. Set None to disable.",
                "すべてのパネルを隠す / 表示するキー。Unity の KeyCode 名（F9、F8、Insert、Home、BackQuote、KeyPad1 など）。None で無効。"));
            changed |= cfg.SetDefault("showPanel", true, ConfigDoc.Of(
                "是否默认显示疑似组队面板。false 时只在日志里输出，按热键仍可唤出。",
                "是否預設顯示疑似組隊面板。false 時只在日誌裡輸出，按熱鍵仍可喚出。",
                "Whether to show the suspected-team panel by default. When false, results go to the log and the hotkey can still reveal it.",
                "疑わしいチームのパネルを最初から表示するか。false ならログのみで、ホットキーで呼び出せます。"));
            changed |= cfg.SetDefault("minTogether", 3, ConfigDoc.Of(
                "近 N 局里同场累计达到多少次算疑似组队（本局也算一次，范围 2-20）。连续两局同场不受此值限制。",
                "近 N 局裡同場累計達到多少次算疑似組隊（本局也算一次，範圍 2-20）。連續兩局同場不受此值限制。",
                "How many shared matches within the recent N games count as a suspected team (current match included, 2-20). Two consecutive matches always qualify.",
                "直近 N 試合で同席が何回以上なら疑わしいチームとみなすか（今回を含む、2-20）。2 試合連続の同席は常に対象。"));
            changed |= cfg.SetDefault("maxRecords", 20, ConfigDoc.Of(
                "每个玩家往回查多少局战绩（范围 1-30）。值越大越准，但请求也越多。",
                "每個玩家往回查多少局戰績（範圍 1-30）。值越大越準，但請求也越多。",
                "How many recent matches to scan per player (1-30). Higher is more accurate but sends more requests.",
                "プレイヤーごとに遡る試合数（1-30）。大きいほど正確ですがリクエストも増えます。"));
            changed |= cfg.SetDefault("selectOnly", false, ConfigDoc.Of(
                "是否只在房间选人页面显示面板。开启后局内不弹面板，结果仍写进日志。",
                "是否只在房間選人頁面顯示面板。開啟後局內不彈面板，結果仍寫進日誌。",
                "Only show the panel on the room hero-select screen. When on, mid-game panels stay hidden but results still go to the log.",
                "ルームのキャラ選択画面でのみパネルを表示する。オンにすると試合中は表示せず、結果はログに残ります。"));
            if (changed || firstLoad) cfg.SaveIfDirty();

            _enabled = cfg.GetBool("enabled", true);
            _showPanel = cfg.GetBool("showPanel", true);
            _selectOnly = cfg.GetBool("selectOnly", false);
            _minTogether = Math.Max(2, Math.Min(20, cfg.GetInt("minTogether", 3)));
            _maxRecords = Math.Max(1, Math.Min(30, cfg.GetInt("maxRecords", 20)));
            _toggleKey = ParseKey(cfg.GetString("toggleKey", "F9"), KeyCode.F9);
            _toggleHeld = false;
        }

        private void WarnLayout()
        {
            if (_warnedLayout) return;
            _warnedLayout = true;
            Log.Warn("游戏程序集布局变了（找不到名片窗口/名片数据的字段或刷新方法），本模组不会生效；"
                + "需要按新版本重新确认字段名与私有方法名。");
        }

        /// <summary>配置里的键名按 KeyCode 解析，写错就退回默认键。</summary>
        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;

            KeyCode key;
            if (Enum.TryParse(name.Trim(), true, out key) && key != KeyCode.None) return key;
            return fallback;
        }
    }
}
