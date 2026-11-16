using System;
using System.Collections.Generic;
using System.Reflection;
using Core.Net;
using GameLogic;
using party.model;
using party.protocol;
using Tools;
using UI;

namespace TeammateCheckMod
{
    /// <summary>一条战绩摘要（从 5154 的 Record[] 里来）。</summary>
    internal struct ShortRecord
    {
        public int Index;
        public long Time;
        public int MapType;
    }

    /// <summary>一场对局：参与者的 UID → 昵称。</summary>
    internal sealed class MatchEntry
    {
        public readonly long Time;
        public readonly int MapType;
        public readonly Dictionary<long, string> Players = new Dictionary<long, string>();

        /// <summary>true = 当前这一局（房间里的人自动算同场），排序时永远放在最后。</summary>
        public bool Current;

        public MatchEntry(long time, int mapType)
        {
            Time = time;
            MapType = mapType;
        }
    }

    /// <summary>某个玩家的历史扫描进度。</summary>
    internal sealed class PlayerHistory
    {
        public long Id;
        public List<ShortRecord> Records;   // null = 还没拿到 5154
        public int Next;                    // 下一条要查详情的 Record 下标
        public int Errors;
        public bool Done;
        public double CompletedAt;

        public bool HasData { get { return Records != null && Next >= Records.Count; } }
    }

    internal sealed class PairCount
    {
        public long A;
        public long B;
        public string NameA;
        public string NameB;
        public int Count;          // 含本局
        public int HistoryCount;   // 只算历史对局
        public bool Consecutive;   // 有连续两局同场
    }

    /// <summary>
    /// 用 5154（看名片）+ 5155（单场详情）静默重建「谁和谁同场过」。
    ///
    /// 5154 的 Record[] 只有自己的名次/英雄，不含队友；同场玩家的 UID 在 5155 的
    /// RecordData（PlayerFightData.PlayerId）里。所以每人要 1 次 5154 + 最多 N 次 5155。
    /// 不调用 AccountLogic.RequestGetShowPlayerC2S（那个会弹名片），直接走 RPC，
    /// 再从 AccountLogic.playerCareer 里读结果；名片窗口开着时整个扫描暂停，避免覆盖。
    /// </summary>
    internal sealed class TeamScanner
    {
        private const double RequestGapSeconds = 0.12;
        private const double PendingTimeoutSeconds = 8.0;
        private const double HistoryTtlSeconds = 1800.0;

        private static readonly FieldInfo CareerField =
            typeof(AccountLogic).GetField("playerCareer", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Action<string> _info;
        private readonly Action<string> _warn;
        private readonly int _maxRecords;

        private readonly Dictionary<long, PlayerHistory> _histories = new Dictionary<long, PlayerHistory>();
        private readonly Dictionary<string, MatchEntry> _matches = new Dictionary<string, MatchEntry>();
        private readonly List<long> _lobby = new List<long>();
        private readonly Dictionary<long, string> _lobbyNames = new Dictionary<long, string>();

        /// <summary>当前这一局：房间里的人此刻就同场。不放进 _matches，免得被当成真记录。</summary>
        private MatchEntry _current;

        private double _nextRequestAt;
        private double _pendingAt;
        private bool _pending;
        private bool _dirty = true;
        private bool _finished;

        public int MinTogether = 3;

        public TeamScanner(int maxRecords, Action<string> info, Action<string> warn)
        {
            _maxRecords = Math.Max(1, Math.Min(30, maxRecords));
            _info = info;
            _warn = warn;
        }

        public bool IsDirty { get { return _dirty; } }

        public int MatchCount { get { return _matches.Count; } }

        public int ScannedPlayers
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _lobby.Count; i++)
                {
                    PlayerHistory h;
                    if (_histories.TryGetValue(_lobby[i], out h) && h.Done) n++;
                }
                return n;
            }
        }

        public int LobbyCount { get { return _lobby.Count; } }

        /// <summary>换一局/换一屋时更新名单；数据缓存保留，新玩家才重新扫。</summary>
        public void SetLobby(IList<long> ids, IList<string> names)
        {
            bool same = ids.Count == _lobby.Count;
            if (same)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    if (ids[i] != _lobby[i]) { same = false; break; }
                }
            }
            if (same) return;

            _lobby.Clear();
            _lobbyNames.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                _lobby.Add(ids[i]);
                _lobbyNames[ids[i]] = (names != null && i < names.Count) ? names[i] : ids[i].ToString();
            }

            // 本局本身就算一次同场：房间里的人此刻都在打同一局。
            MatchEntry current = new MatchEntry(DateTime.UtcNow.Ticks, 0);
            current.Current = true;
            for (int i = 0; i < _lobby.Count; i++) current.Players[_lobby[i]] = NameOf(_lobby[i]);
            _current = current;

            _finished = false;
            _dirty = true;
        }

        public void ClearLobby()
        {
            if (_lobby.Count == 0) return;
            _lobby.Clear();
            _lobbyNames.Clear();
            _current = null;
            _finished = false;
            _dirty = true;
        }

        /// <summary>每帧调用；paused=true 时完全不动（例如名片窗口开着）。</summary>
        public void Tick(double now, bool paused)
        {
            if (paused || _lobby.Count < 2) return;

            if (_pending)
            {
                if (now - _pendingAt <= PendingTimeoutSeconds) return;
                _pending = false;
                _warn("查询超时，跳过一步（可能是服务端没回包）。");
            }

            if (now < _nextRequestAt) return;

            PlayerHistory history = PickNext();
            if (history == null)
            {
                // 全部扫完。这里不能直接 return 掉了事——缓存过期的玩家要能被 PickNext
                // 重新捡起来重扫，所以只把「完成」这件事记一次日志。
                if (!_finished)
                {
                    _finished = true;
                    _dirty = true;
                    _info("扫描完成：本人群 " + _lobby.Count + " 人，命中 " + _matches.Count + " 场对局记录。");
                }
                return;
            }

            if (history.Records == null)
            {
                SendProfile(history, now);
                return;
            }

            if (history.Next < history.Records.Count)
            {
                SendDetail(history, now);
                return;
            }

            history.Done = true;
            history.CompletedAt = now;
            _dirty = true;
        }

        private PlayerHistory PickNext()
        {
            double now = Now;
            for (int i = 0; i < _lobby.Count; i++)
            {
                long id = _lobby[i];
                PlayerHistory history;
                if (!_histories.TryGetValue(id, out history))
                {
                    history = new PlayerHistory { Id = id };
                    _histories[id] = history;
                }
                if (history.Done && now - history.CompletedAt < HistoryTtlSeconds) continue;
                if (history.Done) { history.Done = false; history.Next = 0; history.Records = null; }
                return history;
            }
            return null;
        }

        private static double Now { get { return DateTime.UtcNow.Ticks * 1e-7; } }

        private void SendProfile(PlayerHistory history, double now)
        {
            _pending = true;
            _pendingAt = now;
            _nextRequestAt = now + RequestGapSeconds;

            long id = history.Id;
            RPCAsyncResult result;
            try
            {
                result = MonoSingletonProvider<NetManager>.inst.RPC.GetShowPlayerC2S.GetShowPlayerC2SCall(
                    new GetShowPlayerC2S { PlayerId = id });
            }
            catch (Exception e)
            {
                _pending = false;
                history.Records = new List<ShortRecord>();
                _warn("查询名片失败(" + id + "): " + e.Message);
                return;
            }

            result.OnFinished.AddOnce(delegate(RPCAsyncResult r)
            {
                _pending = false;
                if (r.errId != 0)
                {
                    history.Records = new List<ShortRecord>();
                    _warn("查询名片返回 errId=" + r.errId + "（" + id + "），跳过该玩家。");
                    return;
                }

                PlayerCareer career = ReadCareer();
                if (career == null || career.playerId != id)
                {
                    history.Records = new List<ShortRecord>();
                    _warn("名片数据被别的窗口覆盖，跳过 " + id + "。");
                    return;
                }

                List<ShortRecord> list = new List<ShortRecord>();
                if (career.Records != null)
                {
                    for (int i = 0; i < career.Records.Count && list.Count < _maxRecords; i++)
                    {
                        BattleShortRecord rec = career.Records[i];
                        list.Add(new ShortRecord { Index = rec.Index, Time = rec.Time, MapType = rec.MapType });
                    }
                }

                history.Records = list;
                history.Next = 0;
                if (list.Count == 0) history.Done = true;
                _dirty = true;
            });
        }

        private void SendDetail(PlayerHistory history, double now)
        {
            _pending = true;
            _pendingAt = now;
            _nextRequestAt = now + RequestGapSeconds;

            ShortRecord rec = history.Records[history.Next];
            RPCAsyncResult result;
            try
            {
                result = SimpleSingletonProvider<GameLogicManager>.inst.account
                    .RequestGetPlayerFightRecordC2S(history.Id, rec.Index);
            }
            catch (Exception e)
            {
                _pending = false;
                history.Next++;
                _warn("查询战绩详情失败(" + history.Id + "/" + rec.Index + "): " + e.Message);
                return;
            }

            result.OnFinished.AddOnce(delegate(RPCAsyncResult r)
            {
                _pending = false;
                history.Next++;

                if (r.errId != 0)
                {
                    history.Errors++;
                    return;
                }

                PlayerCareer career = ReadCareer();
                FightRecordDetail detail = career != null ? career.recordDetail : null;
                if (detail == null || detail.rankData == null || detail.rankData.Count == 0) return;

                string key = rec.Time + ":" + rec.MapType;
                MatchEntry entry;
                if (!_matches.TryGetValue(key, out entry))
                {
                    entry = new MatchEntry(rec.Time, rec.MapType);
                    _matches[key] = entry;
                }
                for (int i = 0; i < detail.rankData.Count; i++)
                {
                    PlayerFightData data = detail.rankData[i].data;
                    if (data == null || data.PlayerId <= 100000) continue;   // 过滤 NPC / 占位
                    if (!entry.Players.ContainsKey(data.PlayerId))
                    {
                        entry.Players[data.PlayerId] = string.IsNullOrEmpty(data.Name) ? data.PlayerId.ToString() : data.Name;
                    }
                }
                _dirty = true;
            });
        }

        private static PlayerCareer ReadCareer()
        {
            try
            {
                AccountLogic account = SimpleSingletonProvider<GameLogicManager>.inst.account;
                if (account == null || CareerField == null) return null;
                return CareerField.GetValue(account) as PlayerCareer;
            }
            catch
            {
                return null;
            }
        }

        private string NameOf(long id)
        {
            string name;
            return _lobbyNames.TryGetValue(id, out name) ? name : id.ToString();
        }

        /// <summary>
        /// 当前名单里判为疑似组队的组合，按次数降序。
        ///
        /// 判定：近 N 局同场次数（**含本局**）≥ MinTogether，或者有连续两局同场。
        /// 本局单独记在 _current 里，不混进 _matches，避免它被当成历史记录参与「连场」判断，
        /// 也避免缓存过期重扫时把本局算两遍。
        /// </summary>
        public List<PairCount> Pairs()
        {
            List<MatchEntry> ordered = new List<MatchEntry>(_matches.Values);
            ordered.Sort(delegate(MatchEntry x, MatchEntry y) { return x.Time.CompareTo(y.Time); });

            List<PairCount> result = new List<PairCount>();
            for (int i = 0; i < _lobby.Count; i++)
            {
                for (int j = i + 1; j < _lobby.Count; j++)
                {
                    long a = _lobby[i];
                    long b = _lobby[j];

                    int history = 0;
                    for (int k = 0; k < ordered.Count; k++)
                    {
                        MatchEntry entry = ordered[k];
                        if (entry.Players.ContainsKey(a) && entry.Players.ContainsKey(b)) history++;
                    }

                    bool inCurrent = IsTogetherInCurrent(a, b);
                    int count = history + (inCurrent ? 1 : 0);

                    // 「连续两局同场」按当事人自己的战绩顺序判，而不是全局时间序：
                    // 全局序里夹着别人的局，会漏判；各自的列表里相邻两局都同场才算连场。
                    bool consecutive = AdjacentInHistory(a, b) || AdjacentInHistory(b, a);
                    if (!consecutive && inCurrent) consecutive = TogetherInLastGame(a, b);

                    if (count < MinTogether && !consecutive) continue;

                    result.Add(new PairCount
                    {
                        A = a,
                        B = b,
                        NameA = NameOf(a),
                        NameB = NameOf(b),
                        Count = count,
                        HistoryCount = history,
                        Consecutive = consecutive
                    });
                }
            }
            result.Sort(delegate(PairCount x, PairCount y) { return y.Count.CompareTo(x.Count); });
            return result;
        }

        private bool IsTogetherInCurrent(long a, long b)
        {
            return _current != null && _current.Players.ContainsKey(a) && _current.Players.ContainsKey(b);
        }

        /// <summary>某个玩家自己战绩里相邻的两局，是否都同时有对方。</summary>
        private bool AdjacentInHistory(long self, long other)
        {
            PlayerHistory history;
            if (!_histories.TryGetValue(self, out history)) return false;
            if (history.Records == null || history.Records.Count < 2) return false;

            // career.Records 是按时间降序的，所以从末尾往前就是时间顺序。
            for (int i = history.Records.Count - 1; i > 0; i--)
            {
                MatchEntry later, earlier;
                if (!_matches.TryGetValue(KeyOf(history.Records[i]), out later)) continue;
                if (!_matches.TryGetValue(KeyOf(history.Records[i - 1]), out earlier)) continue;
                if (later.Players.ContainsKey(other) && earlier.Players.ContainsKey(other)) return true;
            }
            return false;
        }

        /// <summary>两人各自「最近一局」里就有对方，而现在这一局又同场 ⇒ 也是连场。</summary>
        private bool TogetherInLastGame(long a, long b)
        {
            return LastGameHas(a, b) || LastGameHas(b, a);
        }

        private bool LastGameHas(long self, long other)
        {
            PlayerHistory history;
            if (!_histories.TryGetValue(self, out history)) return false;
            if (history.Records == null || history.Records.Count == 0) return false;

            MatchEntry last;
            if (!_matches.TryGetValue(KeyOf(history.Records[0]), out last)) return false;
            return last.Players.ContainsKey(self) && last.Players.ContainsKey(other);
        }

        private static string KeyOf(ShortRecord rec)
        {
            return rec.Time + ":" + rec.MapType;
        }

        public void MarkClean()
        {
            _dirty = false;
        }
    }
}
