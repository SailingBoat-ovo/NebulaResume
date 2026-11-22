// ============================================================
// MeetingProxy.cs — 会议中进入: 旧座位在全场按"在场"计, 投票格全场一致
//
// ▸ 第一版的做法(会议期间把旧座位的"已断线"一直清掉)实测不行:
//   旧座位的身体在所有人那里早已销毁, NoS 看到它"没断线"就每帧去读它的位置 → 每帧报错;
//   本插件的失效玩家清扫又把它标回"已断线" → 投票格照样打叉。
//
// ▸ 现在的做法
//   · "已断线"标志平时完全不动。只在 NoS 判定投票格生死的那一瞬间临时清掉, 调用一结束
//     立刻放回去(finalizer, 出错也放回)。这几段代码只看生死, 不碰位置:
//       房主: UpdateButtons / CheckForEndVoting;
//       所有人: UpdatePlayerState(每台客户端各自判生死, 房主每同步一次就重判一次) / SortVotingArea /
//               CanVoteFor(能否投给他: 投票格高亮与点击、选放逐对象) / ModCalculateVotes(计票)。
//     非房主只对"房主回播为在场"的旧座位这样做。
//   · 多人同时代坐: 每个旧座位各自一条记录, 互不影响。
//   · 观战者名牌: 每场会议每人只建一次并记住它, 之后只会把同一个放回去, 不会再新建。
//     → 旧座位按真实生死显示(活着就不打叉), 投票照常被房主收下, 结束投票时也会等它投。
//   · 投票格全场一致(每 0.5 秒检查, 有变化才重新排版):
//       - 代坐者的新身体不是这场会议的人 → 所有人都收起它的投票格(包括代坐者自己);
//       - 会议中进来的观战者 → 所有人都给他补一个死亡状态的名牌(他自己那里也保留)。
//   · 聊天不改记: 新身体就是代坐者本人(同名同装扮), 由新身体直接发言。
//     第一版把聊天改记到旧座位, 而旧座位在别人那里是"已断线=死人", 原版聊天会把
//     死人的话对活人屏蔽 —— 这正是"只有重连者自己看得到"的原因。
//
// ▸ 帧: [14, 旧PlayerId, 新PlayerId, 来源(0=代坐者,1=房主), 旧座位活着(1/0)]
// ============================================================

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace NebulaResume;

internal static class MeetingProxy
{
    internal const int Kind = 14;

    private const float BroadcastInterval = 2f;
    private const float StaleSeconds = 7f;

    private sealed class Entry
    {
        public int OldId;
        public int NewId;
        public float SeenAt;

        /// <summary>房主最近一次回播的旧座位生死(所有客户端都用)。</summary>
        public bool HostAlive;
        public float HostAliveAt = -1000f;
    }

    /// <summary>旧PlayerId → 代坐信息(所有人都记)。</summary>
    private static readonly Dictionary<int, Entry> _byOld = new();

    private static float _nextSendAt;
    private static float _nextHostAt;
    private static float _nextAreaAt;
    private static bool _hostAlive = true;
    private static bool _hasHostAlive;
    private static string _layoutKey = "";

    /// <summary>每场会议只记一次"按房主结果改正"(避免日志刷屏)。</summary>
    private static readonly HashSet<int> _seatFixLogged = new();

    /// <summary>
    ///     本插件给观战者补的名牌(按 PlayerId)。每场会议每人只建一次:
    ///     之后即使 playerStates 被别的代码换掉、或找不到它, 也只把这一个放回去, 绝不再新建。
    /// </summary>
    private static readonly Dictionary<int, PlayerVoteArea> _spectatorAreas = new();

    /// <summary>
    ///     本机会议里补出来的"代坐旧座位"名牌(旧PlayerId → 名牌, 新身体编号)。
    ///     后进会议的人(重连者本人、会议中进入的观战者)是按本机资料重建会议的,
    ///     本机资料里没有旧身份 → 没有旧座位的格子, 新身体那格又被收起 → 谁也看不到重连者。
    /// </summary>
    private static readonly Dictionary<int, (PlayerVoteArea Area, int NewId)> _seatAreas = new();

    /// <summary>
    ///     名牌装扮刷新: 名牌可能在装扮资源载入前就套过一次(显示成无装扮的默认小人)。
    ///     本插件建/改的名牌, 在头 15 秒内每 1.5 秒重套一次, 资料有变化时也重套。
    /// </summary>
    private static readonly Dictionary<int, (float Next, int Count, string Sig)> _plateRefresh = new();

    private static int _areasMeetingId;

    private static void ClearMeetingLocal()
    {
        _seatFixLogged.Clear();
        _spectatorAreas.Clear();
        _platesReady.Clear();
        _seatAreas.Clear();
        _plateRefresh.Clear();
        _spectatorIds.Clear();
        _areasMeetingId = 0;
    }

    internal static void Reset()
    {
        ClearMeetingLocal();
        _byOld.Clear();
        _toggled.Clear();
        _toggleDepth = 0;
        _hasHostAlive = false;
        _nextSendAt = 0f;
        _nextHostAt = 0f;
        _nextAreaAt = 0f;
        _layoutKey = "";
        _chatLogged = 0;
    }

    // ── 收帧 ──

    internal static void OnReceived(int[] frame)
    {
        if (frame == null || frame.Length < 5)
        {
            return;
        }

        var oldId = frame[1];
        var newId = frame[2];
        if (oldId < 0 || oldId > 254 || newId < 0 || newId > 254 || oldId == newId)
        {
            return;
        }

        if (!_byOld.TryGetValue(oldId, out var e))
        {
            e = new Entry { OldId = oldId };
            _byOld[oldId] = e;
            NebulaResumeEntry.Log("会议代坐: 新身体 playerId=" + newId + " 代坐旧座位 playerId=" + oldId + "。");
        }

        e.NewId = newId;
        e.SeenAt = UnityEngine.Time.unscaledTime;

        if (frame[3] == 1)
        {
            e.HostAlive = frame[4] != 0;
            e.HostAliveAt = UnityEngine.Time.unscaledTime;

            if (MeetingSeat.Active && MeetingSeat.OldPlayerId == oldId)
            {
                _hostAlive = e.HostAlive;
                _hasHostAlive = true;
            }
        }
    }

    // ── 每帧 ──

    internal static void Tick()
    {
        var meeting = MeetingHud.Instance;
        var now = UnityEngine.Time.unscaledTime;

        if (meeting == null)
        {
            // 安全复位: 会议期间为判定而临时"在场"的旧座位, 散会后必须恢复为已断线。
            //   万一某次临时切换没有配对恢复(被打断), 旧身份会在 NoS 里一直算活人,
            //   房主的胜负判定(伪装者 ×2 ≥ 存活人数)就永远不成立 → 游戏不结算。
            if (_toggleDepth != 0 || _toggled.Count > 0)
            {
                foreach (var p in _toggled)
                {
                    try { SetDisconnected(p, true); } catch { }
                }

                NebulaResumeEntry.Log("会议代坐: 散会后发现 " + _toggled.Count + " 个旧座位的临时在场标记未恢复(深度 " + _toggleDepth + ") → 已恢复为已断线。");
                _toggled.Clear();
                _toggleDepth = 0;
            }

            if (_byOld.Count > 0 && ExileController.Instance == null)
            {
                _byOld.Clear();
            }

            _hasHostAlive = false;
            _layoutKey = "";
            ClearMeetingLocal();
            return;
        }

        var meetingId = 0;
        try { meetingId = meeting.GetInstanceID(); } catch { }
        if (meetingId != _areasMeetingId)
        {
            ClearMeetingLocal();
            _areasMeetingId = meetingId;
        }

        // 代坐者: 广播自己代坐的座位。
        if (MeetingSeat.Active && now >= _nextSendAt)
        {
            _nextSendAt = now + BroadcastInterval;
            var lp = PlayerControl.LocalPlayer;
            if (lp != null)
            {
                NebulaStateWire.SendRaw(new[] { Kind, MeetingSeat.OldPlayerId, (int)lp.PlayerId, 0, 0 });
            }
        }

        if (MeetingSeat.Active)
        {
            if (_hasHostAlive)
            {
                try { FixLocalSeatArea(meeting); } catch { }
            }

            // 旧座位已经投过票: 本机进入投票阶段时直接按"已投票"处理(房主不会收第二票)。
            try { MeetingDefer.LockIfSeatVoted(meeting); } catch { }
        }

        var client = AmongUsClient.Instance;

        // 非房主: 投票格的生死不随房主同步, 各自本地判定 —— 按房主回播的结果改正旧座位。
        if (client != null && !client.AmHost)
        {
            try { FixSeatsFromHost(meeting, now); } catch { }
        }

        if (client != null && client.AmHost && now >= _nextHostAt)
        {
            _nextHostAt = now + 0.5f;
            try { HostEcho(now); } catch { }
        }

        if (now >= _nextAreaAt)
        {
            _nextAreaAt = now + 0.5f;
            try { TickAreas(meeting); } catch (System.Exception ex) { NebulaResumeEntry.Log("会议名牌: 整理出错: " + ex.Message); }
        }

        try { EnsureSpectatorTags(meeting); } catch { }
    }

    /// <summary>房主: 回播旧座位的真实生死(按"在场"判定)。</summary>
    private static void HostEcho(float now)
    {
        foreach (var e in _byOld.Values)
        {
            if (now - e.SeenAt > StaleSeconds)
            {
                continue;
            }

            var p = Virial.Game.Player.GetPlayer((byte)e.OldId);
            if (p == null)
            {
                continue;
            }

            var alive = false;
            BeginSeatsPresent();
            try { alive = !p.IsDead; } catch { }
            finally { EndSeatsPresent(); }

            NebulaStateWire.SendRaw(new[] { Kind, e.OldId, e.NewId, 1, alive ? 1 : 0 });
        }
    }

    private static void FixSeatsFromHost(MeetingHud meeting, float now)
    {
        if (_byOld.Count == 0)
        {
            return;
        }

        var states = meeting.playerStates;
        if (states == null)
        {
            return;
        }

        foreach (var e in _byOld.Values)
        {
            if (now - e.HostAliveAt > StaleSeconds)
            {
                continue;
            }

            var dead = !e.HostAlive;
            foreach (var area in states)
            {
                if (area == null || area.PlayerId.Value != e.OldId || area.AmDead == dead)
                {
                    continue;
                }

                area.SetDead(dead);
                try { area.Overlay.gameObject.SetActive(dead); } catch { }
                if (_seatFixLogged.Add(e.OldId * 2 + (dead ? 1 : 0)))
                {
                    NebulaResumeEntry.Log("会议代坐: 按房主结果把旧座位 " + e.OldId + " 显示为" + (dead ? "死亡" : "在场") + "。");
                }
            }
        }
    }

    private static void FixLocalSeatArea(MeetingHud meeting)
    {
        var states = meeting.playerStates;
        if (states == null)
        {
            return;
        }

        var dead = !_hostAlive;
        foreach (var area in states)
        {
            if (area == null || area.PlayerId.Value != MeetingSeat.OldPlayerId)
            {
                continue;
            }

            if (area.AmDead != dead)
            {
                area.SetDead(dead);
                try { area.Overlay.gameObject.SetActive(dead); } catch { }
            }
        }
    }

    // ── 房主: 判定生死 / 结束投票的瞬间, 代坐中的旧座位按"在场" ──

    private static readonly List<object> _toggled = new();
    private static int _toggleDepth;

    private static void BeginSeatsPresent()
    {
        if (_toggleDepth++ > 0)
        {
            return;
        }

        var client = AmongUsClient.Instance;
        if (client == null || _byOld.Count == 0 || MeetingHud.Instance == null)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        foreach (var e in _byOld.Values)
        {
            if (now - e.SeenAt > StaleSeconds)
            {
                continue;
            }

            // 非房主: 只按房主回播的结果, 房主说"在场"的旧座位才临时按在场判定。
            if (!client.AmHost && (now - e.HostAliveAt > StaleSeconds || !e.HostAlive))
            {
                continue;
            }

            object? p = null;
            try { p = Virial.Game.Player.GetPlayer((byte)e.OldId); } catch { }
            if (p != null && GetDisconnected(p))
            {
                SetDisconnected(p, false);
                _toggled.Add(p);
            }
        }
    }

    private static void EndSeatsPresent()
    {
        if (_toggleDepth <= 0)
        {
            _toggleDepth = 0;
            return;
        }

        if (--_toggleDepth > 0)
        {
            return;
        }

        foreach (var p in _toggled)
        {
            SetDisconnected(p, true);
        }

        _toggled.Clear();
    }

    private static void SeatsPresentPrefix()
    {
        try { BeginSeatsPresent(); } catch { }
    }

    private static System.Exception? SeatsPresentFinalizer(System.Exception? __exception)
    {
        try { EndSeatsPresent(); } catch { }
        return __exception;
    }

    // ── 投票格: 收起代坐新身体, 给观战者补死亡名牌, 有变化才重排 ──

    private static void TickAreas(MeetingHud meeting)
    {
        var state = meeting.state;
        if (state != MeetingHud.MeetingStates.Discussion
            && state != MeetingHud.MeetingStates.NotVoted
            && state != MeetingHud.MeetingStates.Voted)
        {
            return;   // 开会动画 / 出结果期间不动
        }

        var states = meeting.playerStates;
        if (states == null)
        {
            return;
        }

        // 代坐者的新身体: 不属于这场会议。
        var hide = new HashSet<int>();
        foreach (var e in _byOld.Values)
        {
            hide.Add(e.NewId);
        }

        if (MeetingSeat.Active && PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId != MeetingSeat.OldPlayerId)
        {
            hide.Add(PlayerControl.LocalPlayer.PlayerId);
        }

        // 会议中进来的观战者: 有身体就给他一个死亡名牌。
        var spectators = new List<NetworkedPlayerInfo>();
        try
        {
            var all = PlayerControl.AllPlayerControls;
            if (all != null)
            {
                for (var i = 0; i < all.Count; i++)
                {
                    var pc = all[i];
                    if (pc == null || pc.Data == null || pc.Data.Disconnected)
                    {
                        continue;
                    }

                    var owner = -1;
                    try { owner = pc.OwnerId; } catch { }
                    if (owner >= 0 && SpectatorSeat.IsSpectatorClient(owner) && !hide.Contains(pc.PlayerId))
                    {
                        spectators.Add(pc.Data);
                    }
                }
            }
        }
        catch { }

        _spectatorIds.Clear();
        foreach (var info in spectators)
        {
            _spectatorIds.Add(info.PlayerId);
        }

        var changed = false;

        // 已经离场的观战者: 本插件给他补的名牌一并撤掉(否则会议里留着一个已经走了的人)。
        if (_spectatorAreas.Count > 0)
        {
            var still = new HashSet<int>();
            foreach (var info in spectators)
            {
                still.Add(info.PlayerId);
            }

            List<int>? gone = null;
            foreach (var kv in _spectatorAreas)
            {
                if (!still.Contains(kv.Key))
                {
                    (gone ??= new List<int>()).Add(kv.Key);
                }
            }

            if (gone != null)
            {
                foreach (var id in gone)
                {
                    var area = _spectatorAreas[id];
                    _spectatorAreas.Remove(id);
                    _platesReady.Remove(id);
                    if (area != null)
                    {
                        states = Remove(meeting, states, area);
                        try { UnityEngine.Object.Destroy(area.gameObject); } catch { }
                    }

                    changed = true;
                    NebulaResumeEntry.Log("会议名牌: 观战者 playerId=" + id + " 已离场, 撤掉他的名牌。");
                }
            }
        }

        var present = new HashSet<int>();
        foreach (var area in states)
        {
            if (area == null)
            {
                continue;
            }

            var id = (int)area.PlayerId.Value;
            present.Add(id);

            if (hide.Contains(id))
            {
                if (area.gameObject.activeSelf)
                {
                    area.gameObject.SetActive(false);
                    changed = true;
                }
            }
        }

        // ── 代坐旧座位: 本机会议里没有旧座位的格子时补上 ──
        //   有新身体的格子 → 直接把它改成旧座位(编号改为旧PlayerId, 投它=投旧座位), 外观就是本人;
        //   连新身体的格子也没有 → 用新身体的资料建一个, 编号设为旧座位。
        foreach (var e in _byOld.Values)
        {
            try
            {
                if (_seatAreas.TryGetValue(e.OldId, out var seat) && seat.Area == null)
                {
                    // 被销毁(会议界面重建等): 忘掉它, 下面按现状重新补。
                    _seatAreas.Remove(e.OldId);
                    _plateRefresh.Remove(e.OldId);
                }
                else if (seat.Area != null)
                {

                    var inStates = false;
                    foreach (var area in states)
                    {
                        if (area != null && area.Pointer == seat.Area.Pointer)
                        {
                            inStates = true;
                            break;
                        }
                    }

                    if (!inStates)
                    {
                        states = Append(meeting, states, seat.Area);
                        changed = true;
                    }

                    if (!seat.Area.gameObject.activeSelf)
                    {
                        seat.Area.gameObject.SetActive(true);
                        changed = true;
                    }

                    RefreshPlate(meeting, seat.Area, e.NewId, e.OldId);
                    if (e.HostAliveAt > 0f && !e.HostAlive)
                    {
                        MakeDead(seat.Area);
                    }

                    continue;
                }

                if (present.Contains(e.OldId))
                {
                    continue;   // 本机会议本来就有旧座位(开会时就在场的人): 原有逻辑处理
                }

                PlayerVoteArea? target = null;
                foreach (var area in states)
                {
                    if (area != null && area.PlayerId.Value == e.NewId)
                    {
                        target = area;
                        break;
                    }
                }

                var how = "改用新身体的格子作";
                if (target == null)
                {
                    var info = GameData.Instance != null ? GameData.Instance.GetPlayerById((byte)e.NewId) : null;
                    if (info == null)
                    {
                        continue;   // 新身体资料还没到, 下次再试
                    }

                    target = meeting.CreateButton(info);
                    if (target == null)
                    {
                        continue;
                    }

                    try { target.Parent = meeting; } catch { }
                    states = Append(meeting, states, target);
                    how = "新建";
                }

                target.SetPlayerId((byte)e.OldId);
                target.gameObject.SetActive(true);
                _seatAreas[e.OldId] = (target, e.NewId);
                present.Add(e.OldId);
                RefreshPlate(meeting, target, e.NewId, e.OldId, force: true);
                if (e.HostAliveAt > 0f && !e.HostAlive)
                {
                    MakeDead(target);
                }

                changed = true;
                NebulaResumeEntry.Log("会议名牌: 本机会议里没有代坐旧座位 " + e.OldId + " → " + how + "名牌(新身体 " + e.NewId + "), 编号设为旧座位。");
            }
            catch (System.Exception ex)
            {
                NebulaResumeEntry.Log("会议名牌: 补代坐旧座位出错: " + ex.Message);
            }
        }

        foreach (var info in spectators)
        {
            // 已经给他建过名牌: 只确保它还在 playerStates 里, 绝不重复新建。
            if (_spectatorAreas.TryGetValue(info.PlayerId, out var mine))
            {
                if (mine == null)
                {
                    continue;   // 被销毁(会议重建等): 本场不再补
                }

                var inStates = false;
                foreach (var area in states)
                {
                    if (area != null && area.Pointer == mine.Pointer)
                    {
                        inStates = true;
                        break;
                    }
                }

                if (!inStates)
                {
                    states = Append(meeting, states, mine);
                    changed = true;
                }

                if (!mine.gameObject.activeSelf)
                {
                    mine.gameObject.SetActive(true);
                    changed = true;
                }

                if (!_platesReady.Contains(info.PlayerId))
                {
                    SetupPlate(meeting, mine, info);
                }
                else
                {
                    RefreshPlate(meeting, mine, info.PlayerId, info.PlayerId);
                }

                MakeDead(mine);
                continue;
            }

            if (present.Contains(info.PlayerId))
            {
                // 已有格子(例如观战者自己那里): 保证显示且是死亡态。
                foreach (var area in states)
                {
                    if (area != null && area.PlayerId.Value == info.PlayerId)
                    {
                        if (!area.gameObject.activeSelf)
                        {
                            area.gameObject.SetActive(true);
                            changed = true;
                        }

                        MakeDead(area);
                    }
                }

                continue;
            }

            var created = meeting.CreateButton(info);
            if (created == null)
            {
                continue;
            }

            SetupPlate(meeting, created, info);
            MakeDead(created);
            _spectatorAreas[info.PlayerId] = created;
            states = Append(meeting, states, created);
            changed = true;
            NebulaResumeEntry.Log("会议名牌: 为会议中进入的观战者 playerId=" + info.PlayerId + " 补了一个死亡名牌(格子编号="
                + created.PlayerId.Value + ", 共 " + states.Length + " 格)。");
        }

        // 排版: 只在本插件改了显示的格子之后, 或显示的格子集合变了时。
        var key = VisibleKey(states);
        if (changed || (_layoutKey.Length > 0 && key != _layoutKey))
        {
            Relayout(meeting);
        }

        _layoutKey = key;
    }

    // ── 观战者名牌的「（观战中）」标签 ──
    //   NoS 只有在本机拿到观战职业之后才会在名字后画这个标签; 职业要等观战者身体在各端就位、
    //   再留同步余量才由房主下发, 会议里就会有一段时间看不到标签。
    //   观战名单(服务端下发)一到, 就直接给他的名牌补上标签; 之后职业到了, NoS 画的是同一个标签, 不会重复。
    private static readonly HashSet<int> _spectatorIds = new();

    private static void EnsureSpectatorTags(MeetingHud meeting)
    {
        if (_spectatorIds.Count == 0)
        {
            return;
        }

        var states = meeting.playerStates;
        if (states == null)
        {
            return;
        }

        var tag = SpectatorRole.Instance.SpectatorTag;
        foreach (var area in states)
        {
            if (area == null || !_spectatorIds.Contains(area.PlayerId.Value))
            {
                continue;
            }

            var label = area.NameText;
            if (label == null)
            {
                continue;
            }

            var text = label.text ?? "";
            if (text.Length > 0 && !text.Contains(tag))
            {
                label.text = text + " " + tag;
            }
        }
    }

    /// <summary>名牌装扮已经成功套上的观战者(每场会议)。</summary>
    private static readonly HashSet<int> _platesReady = new();

    /// <summary>
    ///     MeetingHud.CreateButton 只实例化格子, 不设玩家编号、不套装扮(原版由 PopulateButtons 补上):
    ///     直接用它得到的是编号 0、空白的名牌。这里补上编号、所属会议、装扮和名字。
    ///     装扮资源可能还没到(会议中刚生成的身体), 失败就先写名字, 之后每次检查时再试。
    /// </summary>
    private static void SetupPlate(MeetingHud meeting, PlayerVoteArea area, NetworkedPlayerInfo info)
    {
        try
        {
            if (area.PlayerId.Value != info.PlayerId)
            {
                area.SetPlayerId(info.PlayerId);
            }
        }
        catch { }

        try { area.Parent = meeting; } catch { }

        // 会议中刚生成的身体, 资料(名字/装扮)可能还没同步过来: 名字为空就不算完成, 之后再试。
        //   原来只看"名牌文字非空", 房主那边在名字到达前就记成了完成, 名牌一直不完整。
        var name = "";
        try { name = info.PlayerName ?? ""; } catch { }

        var ok = false;
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                area.SetCosmetics(info);
                ok = true;
            }
            catch { }

            try
            {
                if (area.NameText != null && string.IsNullOrEmpty(area.NameText.text))
                {
                    area.NameText.text = name;
                }
            }
            catch { }

            ok = ok && area.NameText != null && !string.IsNullOrEmpty(area.NameText.text);
        }

        if (ok && _platesReady.Add(info.PlayerId))
        {
            NebulaResumeEntry.Log("会议名牌: 观战者 playerId=" + info.PlayerId + " 的名牌已套上装扮与名字(" + info.PlayerName + ")。");
        }
    }

    /// <summary>
    ///     按 sourceId 的资料重套名牌装扮, 名牌编号保持 seatId。
    ///     头 15 秒内每 1.5 秒一次(装扮资源可能晚到), 之后只在资料变化时重套。
    /// </summary>
    private static void RefreshPlate(MeetingHud meeting, PlayerVoteArea area, int sourceId, int seatId, bool force = false)
    {
        NetworkedPlayerInfo? info = null;
        try { info = GameData.Instance != null ? GameData.Instance.GetPlayerById((byte)sourceId) : null; } catch { }
        if (info == null)
        {
            return;
        }

        var name = "";
        try { name = info.PlayerName ?? ""; } catch { }
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var sig = name;
        try
        {
            var o = info.DefaultOutfit;
            sig += "|" + o.ColorId + "|" + o.HatId + "|" + o.SkinId + "|" + o.VisorId + "|" + o.NamePlateId + "|" + o.PetId;
        }
        catch { }

        var now = UnityEngine.Time.unscaledTime;
        var has = _plateRefresh.TryGetValue(seatId, out var st);
        var due = force || !has || st.Sig != sig || (st.Count < 10 && now >= st.Next);
        if (!due)
        {
            return;
        }

        _plateRefresh[seatId] = (now + 1.5f, force || !has ? 0 : st.Count + 1, sig);

        var dead = false;
        try { dead = area.AmDead; } catch { }

        try { area.Parent = meeting; } catch { }
        try { area.SetCosmetics(info); } catch { }
        try
        {
            if (area.PlayerId.Value != seatId)
            {
                area.SetPlayerId((byte)seatId);
            }
        }
        catch { }

        if (dead)
        {
            try { MakeDead(area); } catch { }
        }

        // 套装扮会改写名字, 本机代坐的 "(你)" 标注要重新加。
        if (seatId != sourceId)
        {
            try { MeetingSeat.InvalidateUi(); } catch { }
        }
    }

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea> Append(
        MeetingHud meeting,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea> states,
        PlayerVoteArea area)
    {
        var grown = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea>(states.Length + 1);
        for (var i = 0; i < states.Length; i++)
        {
            grown[i] = states[i];
        }

        grown[states.Length] = area;
        meeting.playerStates = grown;
        return grown;
    }

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea> Remove(
        MeetingHud meeting,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea> states,
        PlayerVoteArea area)
    {
        var keep = new List<PlayerVoteArea>();
        foreach (var a in states)
        {
            if (a != null && a.Pointer != area.Pointer)
            {
                keep.Add(a);
            }
        }

        if (keep.Count == states.Length)
        {
            return states;
        }

        var shrunk = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea>(keep.Count);
        for (var i = 0; i < keep.Count; i++)
        {
            shrunk[i] = keep[i];
        }

        meeting.playerStates = shrunk;
        return shrunk;
    }

    private static void MakeDead(PlayerVoteArea area)
    {
        if (!area.AmDead)
        {
            area.SetDead(true);
        }

        try { area.Overlay.gameObject.SetActive(true); } catch { }
    }

    private static string VisibleKey(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea> states)
    {
        var ids = new List<int>();
        foreach (var area in states)
        {
            if (area != null && area.gameObject.activeSelf)
            {
                ids.Add(area.PlayerId.Value);
            }
        }

        ids.Sort();
        return string.Join(",", ids);
    }

    private static MethodInfo? _toPos;
    private static MethodInfo? _displayType;
    private static float[]? _scales;
    private static bool _layoutResolved;

    private static void ResolveLayout()
    {
        if (_layoutResolved)
        {
            return;
        }

        _layoutResolved = true;
        try
        {
            var rpc = ResumeTransition.Find("Nebula.Patches.MeetingModRpc");
            var helpers = ResumeTransition.Find("Nebula.Patches.More15Helpers");
            _toPos = rpc?.GetMethod("ToVoteAreaPos", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _displayType = helpers?.GetMethod("GetDisplayType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _scales = rpc?.GetField("VotingAreaScale", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as float[];
        }
        catch { }

        if (_toPos == null || _displayType == null)
        {
            NebulaResumeEntry.Log("会议名牌: 找不到 NoS 的投票格排版方法, 新增/收起的格子按原位置显示。");
        }
    }

    /// <summary>按 NoS 的排版规则(活人在前、按编号)重新摆放显示中的投票格。</summary>
    private static void Relayout(MeetingHud meeting)
    {
        ResolveLayout();
        if (_toPos == null || _displayType == null)
        {
            return;
        }

        var visible = new List<PlayerVoteArea>();
        foreach (var area in meeting.playerStates)
        {
            if (area != null && area.gameObject.activeSelf)
            {
                visible.Add(area);
            }
        }

        visible.Sort((a, b) =>
            (a.PlayerId.Value + 32 * (a.AmDead ? 2 : 1)).CompareTo(b.PlayerId.Value + 32 * (b.AmDead ? 2 : 1)));

        var type = (int)_displayType.Invoke(null, new object[] { visible.Count })!;
        var scale = _scales != null && type >= 0 && type < _scales.Length ? _scales[type] : 1f;

        for (var i = 0; i < visible.Count; i++)
        {
            var pos = (UnityEngine.Vector3)_toPos.Invoke(null, new object[] { i, type })!;
            visible[i].transform.localScale = new UnityEngine.Vector3(scale, scale, 1f);
            visible[i].transform.localPosition = pos;
        }
    }

    // ── NoS 的"已断线"标志(PlayerModInfo.IsDisconnected, 可写属性) ──
    private static PropertyInfo? _discProp;

    private static PropertyInfo? DiscProp(object p)
    {
        if (_discProp != null && _discProp.DeclaringType!.IsInstanceOfType(p))
        {
            return _discProp;
        }

        for (var t = p.GetType(); t != null; t = t.BaseType)
        {
            var prop = t.GetProperty("IsDisconnected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (prop != null && prop.CanWrite)
            {
                _discProp = prop;
                return prop;
            }
        }

        return null;
    }

    private static bool GetDisconnected(object p)
    {
        try { return (bool)(DiscProp(p)?.GetValue(p) ?? false); } catch { return false; }
    }

    private static void SetDisconnected(object p, bool value)
    {
        try { DiscProp(p)?.SetValue(p, value); } catch { }
    }

    // ── 代坐投票: 插件通道 [15, 旧座位, 目标, 票数] ──
    //   NoS 自己的投票包照常发出; 这条是兜底: 房主直接替旧座位记票(已投过就忽略)。

    internal const int VoteKind = 15;

    /// <summary>代坐者: 把这一票同时交给房主, 本机界面立刻进入"已投票"。</summary>
    internal static void SendSeatVote(int oldId, byte target, int weight)
    {
        try
        {
            NebulaStateWire.SendRaw(new[] { VoteKind, oldId, (int)target, weight });
        }
        catch { }

        try
        {
            var meeting = MeetingHud.Instance;
            if (meeting == null)
            {
                return;
            }

            foreach (var area in meeting.playerStates)
            {
                if (area != null && area.PlayerId.Value == oldId && !area.DidVote)
                {
                    area.SetVote(target);
                }
            }

            if (meeting.state == MeetingHud.MeetingStates.NotVoted)
            {
                meeting.state = MeetingHud.MeetingStates.Voted;
                try { meeting.SkipVoteButton.ClearButtons(); } catch { }
                foreach (var other in meeting.playerStates)
                {
                    try { other?.ClearButtons(); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>房主: 收到代坐者的票, 记到旧座位上。</summary>
    internal static void OnVoteFrame(int[] frame)
    {
        if (frame == null || frame.Length < 4)
        {
            return;
        }

        var client = AmongUsClient.Instance;
        var meeting = MeetingHud.Instance;
        if (client == null || !client.AmHost || meeting == null)
        {
            return;
        }

        var oldId = frame[1];
        var target = frame[2];
        var weight = frame[3];
        if (oldId < 0 || oldId > 254 || target < 0 || target > 255 || !_byOld.ContainsKey(oldId))
        {
            NebulaResumeEntry.Log("会议代坐(房主): 收到旧座位 " + oldId + " 的票, 但它不在代坐名单里, 忽略。");
            return;
        }

        if (meeting.state != MeetingHud.MeetingStates.NotVoted && meeting.state != MeetingHud.MeetingStates.Voted)
        {
            return;
        }

        PlayerVoteArea? seat = null;
        foreach (var area in meeting.playerStates)
        {
            if (area != null && area.PlayerId.Value == oldId)
            {
                seat = area;
            }
        }

        if (seat == null)
        {
            return;
        }

        if (seat.DidVote)
        {
            NebulaResumeEntry.Log("会议代坐(房主): 旧座位 " + oldId + " 的票已经记上(NoS 通道已生效)。");
            return;
        }

        var dead = true;
        BeginSeatsPresent();
        try
        {
            var p = Virial.Game.Player.GetPlayer((byte)oldId);
            dead = p == null || p.IsDead;
        }
        catch { }
        finally { EndSeatsPresent(); }

        if (dead)
        {
            NebulaResumeEntry.Log("会议代坐(房主): 旧座位 " + oldId + " 已真实死亡, 不计票。");
            return;
        }

        try
        {
            var ext = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension");
            if (ext?.GetField("WeightMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is Dictionary<byte, int> map)
            {
                map[(byte)oldId] = weight;
            }
        }
        catch { }

        seat.SetVote((byte)target);
        meeting.SetDirtyBit(1U);
        NebulaResumeEntry.Log("会议代坐(房主): 已替旧座位 " + oldId + " 记票 → 目标 " + target + "(票数 " + weight + ")。");
        try { meeting.CheckForEndVoting(); } catch { }
    }

    /// <summary>
    ///     原版 MeetingHud.DidVote 用 First() 找投票格, 找不到就抛异常。
    ///     聊天气泡会用它判断发言者是否已投票: 代坐者的新身体在别人的会议里没有投票格 →
    ///     抛异常 → 原版聊天把这条消息整条丢掉(这就是"别人看不到重连者发言"的原因)。
    ///     找不到投票格时按"没投票"返回。
    /// </summary>
    private static bool DidVotePrefix(MeetingHud __instance, byte playerId, ref bool __result)
    {
        try
        {
            var states = __instance.playerStates;
            if (states != null)
            {
                foreach (var area in states)
                {
                    if (area != null && area.PlayerId.Value == playerId)
                    {
                        return true;   // 有投票格: 走原版
                    }
                }
            }

            __result = false;
            return false;
        }
        catch
        {
            return true;
        }
    }

    // ── 安装 ──

    internal static void Install(Harmony harmony)
    {
        try
        {
            var didVote = AccessTools.Method(typeof(MeetingHud), nameof(MeetingHud.DidVote));
            if (didVote != null)
            {
                harmony.Patch(didVote, prefix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(DidVotePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                NebulaResumeEntry.Log("会议代坐: 已挂 MeetingHud.DidVote(没有投票格的发言者不再让聊天整条丢失)。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议代坐: 挂载 DidVote 失败: " + e.Message);
        }

        PatchSeatsPresent(harmony, ResumeTransition.Find("Nebula.Patches.MeetingHudUpdatePatch")
            ?.GetMethod("UpdateButtons", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public), "UpdateButtons");
        PatchSeatsPresent(harmony, AccessTools.Method(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting)), "CheckForEndVoting");

        // 每台客户端各自判定投票格生死(房主同步一次就重判一次), 以及开会 2 秒后的排序:
        //   不在这里按"在场"判定, 旧座位就会在别人那里被改回死亡, 和我们的改正来回打架。
        PatchSeatsPresent(harmony, ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension")
            ?.GetMethod("UpdatePlayerState", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public), "UpdatePlayerState");
        PatchSeatsPresent(harmony, ResumeTransition.Find("Nebula.Patches.MeetingModRpc")
            ?.GetMethod("SortVotingArea", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public), "SortVotingArea");

        // 能不能投给某人: NoS 在投票格高亮/点击(CanBeHighlighted 补丁)、计票、选放逐对象时都用
        //   CanVoteFor(目标), 里面要求目标"没死" —— 旧座位按断线算死, 所以谁都投不了他。
        //   计票本身(ModCalculateVotes)也只数"没死"的投票格。两处都按在场判定。
        var ext = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension");
        if (ext != null)
        {
            foreach (var m in ext.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (m.Name == "CanVoteFor" && m.GetParameters().Length == 1)
                {
                    PatchSeatsPresent(harmony, m, "CanVoteFor(" + m.GetParameters()[0].ParameterType.Name + ")");
                }
            }
        }
        else
        {
            NebulaResumeEntry.Log("会议代坐: 找不到 MeetingHudExtension, 旧座位可能无法被投票。");
        }

        PatchSeatsPresent(harmony, ResumeTransition.Find("Nebula.Patches.CheckForEndVotingPatch")
            ?.GetMethod("ModCalculateVotes", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public), "ModCalculateVotes");

        // 代坐者自己的机器: 本机 NoS 是会议中重建的, 只注册了还有身体的玩家 ——
        //   旧座位(身体已销毁)在本机 NoS 里根本不存在, CanVoteFor(旧座位) 一律 false,
        //   所以"他自己投不了他自己"。这里按房主回播的生死补判。
        if (ext != null)
        {
            foreach (var m in ext.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var ps = m.GetParameters();
                if (m.Name == "CanVoteFor" && ps.Length == 1 && ps[0].ParameterType == typeof(byte))
                {
                    try
                    {
                        harmony.Patch(m, postfix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(CanVoteForPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                        NebulaResumeEntry.Log("会议代坐: 已挂 CanVoteFor 补判(本机 NoS 里没有旧座位时按房主结果)。");
                    }
                    catch (System.Exception e)
                    {
                        NebulaResumeEntry.Log("会议代坐: 挂载 CanVoteFor 补判失败: " + e.Message);
                    }
                }
            }
        }

        InstallExileGuards(harmony);

        try
        {
            MethodInfo? target = null;
            foreach (var m in typeof(ChatController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "AddChat")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 2 && ps[0].ParameterType == typeof(PlayerControl))
                {
                    target = m;
                    break;
                }
            }

            if (target != null)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(AddChatPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议代坐: 挂载聊天诊断失败: " + e.Message);
        }
    }

    private static void PatchSeatsPresent(Harmony harmony, MethodInfo? target, string name)
    {
        if (target == null)
        {
            NebulaResumeEntry.Log("会议代坐: 找不到 " + name + ", 旧座位在该判定里仍按掉线处理。");
            return;
        }

        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(SeatsPresentPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(SeatsPresentFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            NebulaResumeEntry.Log("会议代坐: 已挂 " + name + "(代坐旧座位按在场判定)。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议代坐: 挂载 " + name + " 失败: " + e.Message);
        }
    }

    // ── CanVoteFor 补判 ──

    private static FieldInfo? _voteForMaskField;
    private static FieldInfo? _sealedMaskField;
    private static bool _masksResolved;

    private static void CanVoteForPostfix(byte playerId, ref bool __result)
    {
        if (__result)
        {
            return;
        }

        try
        {
            if (!_byOld.TryGetValue(playerId, out var e) || MeetingHud.Instance == null)
            {
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            var client = AmongUsClient.Instance;
            var alive = client != null && client.AmHost
                ? now - e.SeenAt <= StaleSeconds
                : now - e.HostAliveAt <= StaleSeconds && e.HostAlive;
            if (!alive)
            {
                return;
            }

            // 只补"本机 NoS 里没有这名玩家"的情况; 有的话原判定(含切换)已经算过, 不改。
            if (Virial.Game.Player.GetPlayer(playerId) != null)
            {
                return;
            }

            if (!_masksResolved)
            {
                _masksResolved = true;
                var ext = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension");
                _voteForMaskField = ext?.GetField("VoteForMask", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _sealedMaskField = ext?.GetField("SealedMask", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }

            var bit = 1 << playerId;
            var voteFor = _voteForMaskField?.GetValue(null) is int vf ? vf : -1;
            var sealedMask = _sealedMaskField?.GetValue(null) is int sm ? sm : 0;
            __result = (voteFor & bit) != 0 && (sealedMask & bit) == 0;
        }
        catch { }
    }

    // ── 放逐: 被投出的是代坐中的旧座位 ──
    //
    //   实测: 大家把票投给重连者(旧座位), 放逐动画黑屏卡死, 会议再也结束不了。
    //   原因(NoS 的放逐流程):
    //     · 结果里按 PlayerControl 找被放逐者 —— 旧座位的身体早已销毁, 找不到 → 动画的主角为空;
    //     · 多人放逐动画遍历"被放逐名单", 名单里的旧座位在本机 GameData / NoS 里取不到
    //       (代坐者自己那台机器上 NoS 里压根没有旧座位) → 空引用 → 动画协程返回空 →
    //       ExileController.Begin 报 "routine is null", 放逐停在黑屏。
    //   做法:
    //     · 结果到达时(所有人): 被放逐的是代坐中的旧座位且主角为空 → 主角用旧座位的资料;
    //       旧座位资料也没了就用他新身体的资料(同一个人、同样的外观)。
    //     · 多人放逐动画开始前: 名单里暂时去掉本机取不到资料的人(只影响这一段动画), 结束后放回。
    //     · 代坐者本人: 记下"本场会议被放逐", 散会后正式续局时按灵魂恢复(否则会按掉线前存活复活)。

    private static FieldInfo? _exiledCacheField;

    private static void InstallExileGuards(Harmony harmony)
    {
        try
        {
            var rpc = ResumeTransition.Find("Nebula.Patches.MeetingModRpc");
            var complete = rpc?.GetMethod("ForcelyVotingComplete", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (complete != null)
            {
                harmony.Patch(complete, postfix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(VotingCompletePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                NebulaResumeEntry.Log("会议代坐: 已挂投票结果(被放逐的旧座位改用可显示的资料)。");
            }
            else
            {
                NebulaResumeEntry.Log("会议代坐: 找不到 ForcelyVotingComplete, 放逐旧座位时动画可能卡住。");
            }

            _exiledCacheField = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension")
                ?.GetField("ExiledAllModCache", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            System.Type? helper = null;
            if (rpc != null)
            {
                foreach (var t in rpc.Assembly.GetTypes())
                {
                    if (t.Name.EndsWith("MultipleExileHelper"))
                    {
                        helper = t;
                        break;
                    }
                }
            }

            var spawn = helper?.GetMethod("SpawnMultiplePlayers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (spawn != null && _exiledCacheField != null)
            {
                harmony.Patch(spawn,
                    prefix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(SpawnExiledPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(SpawnExiledFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                NebulaResumeEntry.Log("会议代坐: 已挂多人放逐动画保护(取不到资料的人不进动画)。");
            }
            else
            {
                NebulaResumeEntry.Log("会议代坐: 找不到多人放逐动画, 未挂保护。");
            }

            // 放逐画面开始前再查一遍(排在 NoS 自己的处理之前), 名单里不能有空位。
            var begin = typeof(ExileController).GetMethod("Begin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (begin != null && _exiledCacheField != null)
            {
                harmony.Patch(begin, prefix: new HarmonyMethod(typeof(MeetingProxy).GetMethod(nameof(ExileBeginPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                NebulaResumeEntry.Log("会议代坐: 已挂放逐名单空位保护。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议代坐: 挂载放逐保护失败: " + e.Message);
        }
    }

    private static void VotingCompletePostfix(MeetingHud meetingHud, byte exiled, byte[] exiledAll)
    {
        try
        {
            if (exiledAll != null && MeetingSeat.Active)
            {
                foreach (var id in exiledAll)
                {
                    if (id == MeetingSeat.OldPlayerId)
                    {
                        MeetingSeat.NoteExiled();
                    }
                }
            }

            SanitizeExiledCache(exiledAll, "投票结果");

            if (meetingHud == null || !_byOld.TryGetValue(exiled, out var e))
            {
                return;
            }

            var current = meetingHud.exiledPlayer;
            if (current != null && current.PlayerId == exiled)
            {
                return;
            }

            // 所有机器统一用新身体(同一个人、同样外观, 而且身体还在): 动画能正常播,
            //   放逐结束时原版会让这具新身体按"被放逐"死亡, 全场一致。
            //   新身体资料取不到时才退回旧座位资料。
            var gd = GameData.Instance;
            var info = gd != null ? gd.GetPlayerById((byte)e.NewId) : null;
            var source = "新身体 playerId=" + e.NewId + " 的资料";
            if ((info == null || info.Object == null) && gd != null)
            {
                info = gd.GetPlayerById(exiled);
                source = "旧座位资料";
            }

            if (info != null)
            {
                meetingHud.exiledPlayer = info;
                NebulaResumeEntry.Log("会议代坐: 被放逐的是代坐中的旧座位 " + exiled + ", 放逐动画改用" + source + "。");
            }
            else
            {
                NebulaResumeEntry.Log("会议代坐: 被放逐的是旧座位 " + exiled + ", 但本机取不到任何资料, 放逐动画不显示主角。");
            }
        }
        catch (System.Exception ex)
        {
            NebulaResumeEntry.Log("会议代坐: 处理投票结果出错: " + ex.Message);
        }
    }

    // ── 放逐名单空位 ──
    //   NoS 收到投票结果时按编号在本机查人: ExiledAllModCache = exiledAll.Select(Player.GetPlayer)。
    //   会议中才进来的人(重连者 / 观战者)本机没有被代坐的旧座位, 查出来是空,
    //   随后 NoS 的放逐画面(ExileControllerBeginPatch.Postfix)和散会流程(NebulaExileWrapUp)
    //   逐个读名单 → 空引用 → 散会协程中断, 本机永远不发"放逐同步"。
    //   房主的散会同步要等所有人(没有超时), 于是整房卡在放逐黑屏, 直到这些人被踢。
    //   做法: 空位如果是代坐中的旧座位 → 换成他的新身体(同一个人, 与放逐动画一致);
    //        否则去掉这个空位。名单里本来就有的人不动。
    private static void SanitizeExiledCache(byte[]? exiledAll, string when)
    {
        try
        {
            if (_exiledCacheField?.GetValue(null) is not Virial.Game.Player?[] cache || cache.Length == 0)
            {
                return;
            }

            var hasNull = false;
            foreach (var p in cache)
            {
                if (p == null)
                {
                    hasNull = true;
                    break;
                }
            }

            if (!hasNull)
            {
                return;
            }

            var keep = new List<Virial.Game.Player>();
            var present = new HashSet<byte>();
            foreach (var p in cache)
            {
                if (p == null)
                {
                    continue;
                }

                try { present.Add(p.PlayerId); } catch { }
                keep.Add(p);
            }

            var notes = new List<string>();
            for (var i = 0; i < cache.Length; i++)
            {
                if (cache[i] != null)
                {
                    continue;
                }

                int id = exiledAll != null && i < exiledAll.Length ? exiledAll[i] : -1;
                Virial.Game.Player? sub = null;
                if (id >= 0 && _byOld.TryGetValue(id, out var e))
                {
                    try { sub = Virial.Game.Player.GetPlayer((byte)e.NewId); } catch { }
                    if (sub != null && present.Add((byte)e.NewId))
                    {
                        keep.Add(sub);
                        notes.Add(id + "→新身体 " + e.NewId);
                        continue;
                    }
                }

                notes.Add((id >= 0 ? id.ToString() : "?") + " 去掉");
            }

            _exiledCacheField.SetValue(null, keep.ToArray());
            NebulaResumeEntry.Log("会议代坐(" + when + "): 放逐名单里有本机查不到的人 [" + string.Join(", ", notes)
                + "] → 已修正, 防止散会流程空引用卡死。");
        }
        catch (System.Exception ex)
        {
            NebulaResumeEntry.Log("会议代坐: 修正放逐名单出错: " + ex.Message);
        }
    }

    private static void ExileBeginPrefix()
    {
        SanitizeExiledCache(null, "放逐画面");
    }

    private static object? _exiledCacheSaved;
    private static bool _exiledCacheSwapped;

    private static void SpawnExiledPrefix()
    {
        _exiledCacheSwapped = false;
        try
        {
            if (_exiledCacheField?.GetValue(null) is not Virial.Game.Player?[] cache || cache.Length == 0)
            {
                return;
            }

            var gd = GameData.Instance;
            var keep = new List<Virial.Game.Player>();
            var dropped = new List<string>();
            foreach (var p in cache)
            {
                if (p == null)
                {
                    dropped.Add("空");
                    continue;
                }

                byte id;
                try { id = p.PlayerId; } catch { dropped.Add("?"); continue; }

                if (gd == null || gd.GetPlayerById(id) == null)
                {
                    dropped.Add(id.ToString());
                    continue;
                }

                keep.Add(p);
            }

            if (dropped.Count == 0)
            {
                return;
            }

            _exiledCacheSaved = cache;
            _exiledCacheSwapped = true;
            _exiledCacheField.SetValue(null, keep.ToArray());
            NebulaResumeEntry.Log("会议代坐: 放逐动画里暂时去掉本机取不到资料的 [" + string.Join(",", dropped) + "](只影响动画)。");
        }
        catch { }
    }

    private static System.Exception? SpawnExiledFinalizer(System.Exception? __exception)
    {
        try
        {
            if (_exiledCacheSwapped)
            {
                _exiledCacheField?.SetValue(null, _exiledCacheSaved);
            }
        }
        catch { }

        _exiledCacheSwapped = false;
        _exiledCacheSaved = null;

        if (__exception != null)
        {
            // 动画里出错会让整个放逐卡在黑屏(协程返回空): 记下来并吞掉, 让放逐流程继续。
            NebulaResumeEntry.Log("会议代坐: 多人放逐动画出错已拦下(放逐继续): " + __exception.GetType().Name + " : " + __exception.Message);
        }

        return null;
    }

    private static int _chatLogged;

    /// <summary>诊断: 会议期间代坐者新身体的聊天到了本机(只记录, 不改动)。</summary>
    private static void AddChatPrefix([HarmonyArgument(0)] PlayerControl sourcePlayer)
    {
        try
        {
            if (_chatLogged >= 20 || sourcePlayer == null || MeetingHud.Instance == null || _byOld.Count == 0)
            {
                return;
            }

            var src = (int)sourcePlayer.PlayerId;
            foreach (var e in _byOld.Values)
            {
                if (e.NewId != src)
                {
                    continue;
                }

                _chatLogged++;
                var lp = PlayerControl.LocalPlayer;
                NebulaResumeEntry.Log("会议聊天: 收到代坐者(新身体 playerId=" + src + ")的发言 —— 发言者资料="
                    + (sourcePlayer.Data != null) + " 发言者死亡=" + (sourcePlayer.Data != null && sourcePlayer.Data.IsDead)
                    + " 本机死亡=" + (lp != null && lp.Data != null && lp.Data.IsDead) + "。");
                return;
            }
        }
        catch { }
    }
}
