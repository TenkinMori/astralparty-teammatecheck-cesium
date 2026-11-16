using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using GameLogic;
using Tools;
using UI;

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
    /// 游戏里「隐藏资料 / 隐藏战绩」是账号上的两个开关（ShowPlayer.IsShowData、
    /// ShowPlayer.IsShowFight），由服务端保存；别人打开你的名片时，服务端仍然把
    /// 整份 ShowPlayerInfo（含统计与战绩列表）发给客户端，客户端在
    /// UI.AccountInfoWindow 里按 IsSelf || isShowX 决定要不要渲染，隐藏时只显示
    /// 一句「对方已隐藏」的提示。
    ///
    /// 本模组不改游戏文件、也不伪造数据：只在打开别人的名片时，把
    /// PlayerCareer.isShowData / isShowFight 临时置为 true 并重刷界面，让本地把
    /// 已经收到的数据画出来；名片关掉后按原值还原。如果服务端确实没下发数据
    /// （statistics 为 null、Records 为 null），这里也不会凭空造出内容，只在日志里
    /// 记一条 —— 这条日志正好能实测服务端有没有裁剪。
    ///
    /// 自己的名片（IsSelf）一律不碰，保证「隐藏」开关仍然照常工作。
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
        private double _nextPoll;
        private bool _warnedLayout;
        private bool _warnedNoData;

        private long _appliedPlayerId;
        private PlayerCareer _appliedCareer;
        private bool _savedShowData;
        private bool _savedShowFight;

        public override string Name { get { return "对队友进行大调查"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "TenkinMori"; } }
        public override string Description
        {
            get { return "别人隐藏的资料和战绩，在名片上照样摊开看；纯本地解除显示限制，数据仍以服务端下发为准。"; }
        }

        public override void OnInitialize()
        {
            ReadConfig(true);
            Log.Info("配置: enabled=" + _enabled
                + "，名片窗口字段=" + (WindowField != null)
                + "，名片数据字段=" + (CareerField != null)
                + "，统计刷新=" + (RefreshStatMethod != null)
                + "，战绩刷新=" + (RefreshFightMethod != null));
            if (WindowField == null || CareerField == null) WarnLayout();
        }

        public override void OnConfigChanged()
        {
            ReadConfig(false);
            if (!_enabled) Restore();
            Log.Info("配置已更新: enabled=" + _enabled);
        }

        public override void OnUnload()
        {
            Restore();
        }

        public override void OnUpdate()
        {
            double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;

            try { Tick(); }
            catch (Exception e) { Log.Warn("巡检失败: " + e.Message); }
        }

        private void Tick()
        {
            if (!_enabled) return;
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

        private void ReadConfig(bool writeDefaults)
        {
            ModConfig cfg = Config;
            if (cfg == null) return;

            if (writeDefaults && cfg.Count == 0)
            {
                cfg.Set("enabled", true);
                cfg.SaveIfDirty();
            }

            _enabled = cfg.GetBool("enabled", true);
        }

        private void WarnLayout()
        {
            if (_warnedLayout) return;
            _warnedLayout = true;
            Log.Warn("游戏程序集布局变了（找不到名片窗口/名片数据的字段或刷新方法），本模组不会生效；"
                + "需要按新版本重新确认字段名与私有方法名。");
        }
    }
}
