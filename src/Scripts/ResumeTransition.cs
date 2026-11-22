// ============================================================
// ResumeTransition.cs — 续局过渡(外科式)
//
// ▸ 本模块的做法: 重连之后不重新加载场景, 而是把本机状态
//   【就地改成"正在对局中"】。五个动作:
//
//     ① auc.GameState = Started        让游戏以为已经开局
//     ② 隐藏 LobbyBehaviour             大厅那个"自由活动"的房间
//     ③ 隐藏 GameStartManager           大厅 UI(房间号/设置/开始按钮)
//     ④ HudManager.SetHudActive(true)   打开游戏内 HUD
//     ⑤ 重建本机 NoS 层                 把全场玩家注册进 NoS + 切到 Initialized
//
// ▸ 为什么需要 ①②③④
//   重连回来时客户端的 UI 可能停在大厅态(尤其是"回主菜单后重新加入"这条路),
//   光进了场景但 UI 还是大厅的样子 —— 玩家看到"没有 HUD、动不了"。
//   这四步把 UI 状态对齐到"在对局中"。
//
// ▸ 为什么需要 ⑤
//   NoS 对"中途进来的玩家"没有游戏实例时, 凡是碰本地 NoS 玩家的系统都会每帧空引用。
//   所以必须把全场玩家注册进本机 NoS, 并把 NoS 阶段切到 Initialized
//   (否则 HUD 停在大厅态、职业显示 unassigned)。
//
// ▸ ⚠️ 诚实说明: NebulaGameManager 是 NOS 的 internal 类型, 所以第 ⑤ 步必须走反射。
//   这与设计取向无关: 这个类根本没有公开入口, 只能走反射。
//   第 ①②③④ 步用的是公开 API, 没有反射。
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class ResumeTransition
{
    private static bool _done;

    /// <summary>本局是否已经做过过渡(避免重复执行)。</summary>
    internal static bool Done => _done;

    // ── 待执行请求 ──
    //
    //   会议状态要靠房主的周期广播才能知道(见 MeetingFlag), 而续局信号是
    //   一进来就到的。所以"请求执行过渡"和"真正执行"必须分开:
    //   拿到会议状态之前先挂起, 有结论了(或等够了)再执行。
    private static bool _pendingRun;
    private static float _requestedAt;
    private static float _lastTryAt;

    /// <summary>等房主会议状态的最长时间(秒)。超过就按"不在会议中"继续。</summary>
    private const float WaitForMeetingInfo = 3f;

    /// <summary>会议结束后, 房主状态广播还没刷成 0 时的等待窗口(秒)。</summary>
    private const float MeetingRefuseGraceSeconds = 10f;

    /// <summary>挂起期间的重试间隔(秒)。</summary>
    private const float RetryInterval = 0.2f;

    /// <summary>等本机角色就位的最长时间(秒)。超过就照常继续(后续步骤会补做)。</summary>
    private const float WaitForLocalBody = 20f;

    private static float _waitLogAt;

    /// <summary>
    ///     本机角色是否已经可以承接续局: 地图在、本机角色在、资料在、原版职业在。
    /// </summary>
    internal static bool LocalBodyReady(out string why)
    {
        why = "";
        try
        {
            if (ShipStatus.Instance == null) { why = "地图未加载"; return false; }
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) { why = "本机角色未生成"; return false; }
            if (lp.Data == null) { why = "本机资料未同步"; return false; }
            if (lp.Data.Role == null) { why = "本机原版职业未就位"; return false; }
            return true;
        }
        catch (System.Exception e)
        {
            why = "检查异常 " + e.GetType().Name;
            return false;
        }
    }

    /// <summary>本机玩家是否已经注册进本机 NoS。</summary>
    internal static bool LocalInNoS()
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            return lp != null && Virial.Game.Player.GetPlayer(lp.PlayerId) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     兜底: 本机玩家不在 NoS 里 / NoS 还没切到对局阶段时补做一次(只补缺的部分)。
    ///     由恢复包重试、会议延迟搭建调用。
    /// </summary>
    internal static void EnsureLocalNoS()
    {
        if (_nosStarted && LocalInNoS())
        {
            return;
        }

        var lp = PlayerControl.LocalPlayer;
        if (lp == null || lp.Data == null)
        {
            return;
        }

        RebuildLocalNoS();
    }

    /// <summary>异常的真正原因(反射调用会包一层 TargetInvocationException)+ 第一帧位置。</summary>
    internal static string Describe(System.Exception e)
    {
        var inner = e;
        while (inner is TargetInvocationException && inner.InnerException != null)
        {
            inner = inner.InnerException;
        }

        var frame = "";
        try
        {
            var st = inner.StackTrace;
            if (!string.IsNullOrEmpty(st))
            {
                var line = st.Split('\n')[0].Trim();
                frame = " @ " + (line.Length > 160 ? line.Substring(0, 160) : line);
            }
        }
        catch { }

        return inner.GetType().Name + " : " + inner.Message + frame;
    }

    /// <summary>进入新对局时复位, 让下一次重连还能做过渡。</summary>
    internal static void Reset()
    {
        _done = false;
        EndPending();
        _requestedAt = 0f;
        _lastTryAt = 0f;
        _hideLogged.Clear();
        _hideUiAt = 0f;
        _diagAt = 0f;
        _nosStarted = false;
        ClearRefusal();
    }

    /// <summary>
    ///     本机 NoS 层是否已经切到对局阶段(OnGameStart 只能调一次)。
    ///     只随新对局复位(<see cref="Reset"/>), 收到续局信号的 BeginAttempt 不复位。
    /// </summary>
    private static bool _nosStarted;

    /// <summary>
    ///     会议界面即将在一台"中途进来、还没续局"的机器上搭建: 先把 NoS 层准备好。
    /// </summary>
    /// <remarks>
    ///     会议中进来的重连者会随房间数据收到会议对象, 而本机的 NoS 玩家表此时还是空的 ——
    ///     模组的开会补丁按玩家读职业、外观, 读到空就中断, 会议界面只搭一半。
    ///     这里提前做续局里"重建 NoS 层"那一步(注册全场玩家 + 占位职业 + 切到对局阶段),
    ///     其余步骤(界面切换、位置、职业恢复)仍然等散会后的正式续局。
    ///     判据不依赖任何消息: 对局场景已经在(有 ShipStatus), 而本机 NoS 还没开始。
    /// </remarks>
    internal static void PrepareForMeeting()
    {
        if (_done || _nosStarted || ShipStatus.Instance == null)
        {
            return;
        }

        // 本机角色还没生成时不做: NoS 开局要读本机玩家, 读到空会半初始化且不能重来。
        //   这种情况由 MeetingDefer 先把会议界面的搭建推迟, 角色到了再调本方法。
        var lpNow = PlayerControl.LocalPlayer;
        if (lpNow == null || lpNow.Data == null)
        {
            return;
        }

        try
        {
            var ngmType = FindType("Nebula.Game.NebulaGameManager");
            var ngm = ngmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var state = ngm == null ? null : ngmType!.GetProperty("GameState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ngm);
            if (ngm == null || state == null || state.ToString() != "NotStarted")
            {
                return;   // 正常开局的机器: NoS 早已在对局阶段
            }

            NebulaResumeEntry.Log("会议中进入: 会议界面搭建前先重建本机 NoS 层。");
            RebuildLocalNoS();
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议中进入: 预备 NoS 层失败: " + e.Message);
        }
    }

    // ══════════════════════════════════════════════════════════
    // 会议期间拒绝重连
    // ══════════════════════════════════════════════════════════
    //
    // ▸ 为什么要拒绝而不是"等散会再做"
    //   会议界面(MeetingHud)是会议开始那一刻按【当时在场的玩家】一次性搭好的
    //   定长结构: 投票格数量、座位顺序、放逐动画的参与者都是固定的。
    //   中途插入的玩家在这套结构里没有位置。往里面塞的结果不是"晚一点显示",
    //   而是一份【半初始化的界面】—— 而且它不会自己恢复。
    //
    //   所以本次进房一旦撞上会议, 就直接作废: 不做过渡、不应用状态包、
    //   不碰会议对象, 并明确告诉玩家等散会后重新进入服务器。

    /// <summary>本次进房是否因会议进行中而被拒绝重连。</summary>
    internal static bool RefusedByMeeting { get; private set; }

    /// <summary>被拒绝后待执行的"移出房间"(下一次 Tick 立即执行)。</summary>
    private static bool _kickPending;

    /// <summary>
    ///     把本次进房标记为"会议进行中, 拒绝重连", 并立即把本机移出房间。
    ///     标上之后 <see cref="Run"/> 在本机离开房间之前都不会再尝试过渡。
    ///     提示不在房间里弹(人马上就被移出去了), 而是回到主菜单后由
    ///     ReconnectPrompt 用原生掉线弹窗显示。
    /// </summary>
    internal static void RefuseByMeeting(string source)
    {
        if (RefusedByMeeting)
        {
            return;
        }

        RefusedByMeeting = true;
        EndPending();

        // 被拒 = 这次重连失败: 回到主菜单后要按"会议中重连被拒"提示(且不计入上限)。
        ReconnectPrompt.MarkReconnectFailed(source, true);
        _kickPending = true;
        NebulaResumeEntry.Log("重连拒绝(" + source
            + "): 会议进行中 → 不做过渡, 不应用状态, 立即把本机移出房间。");
    }

    /// <summary>清掉拒绝标记(离开房间后调用, 否则下一次加入会一直被拒)。</summary>
    internal static void ClearRefusal()
    {
        RefusedByMeeting = false;
        _kickPending = false;
    }

    /// <summary>
    ///     由 Entry.Tick 调用: 拒绝之后立即把玩家移出房间(回主菜单)。
    ///
    ///   ▸ 为什么必须踢
    ///     被拒绝的玩家此时【已经进了房间】, 但不参与这一局。
    ///     留在里面既看不到对局也没法玩, 只会占着一个玩家位;
    ///     如果房主已经给他补生成了身体, 他还会让全场的同步握手一直等他。
    /// </summary>
    internal static void TickRefusalKick()
    {
        if (!RefusedByMeeting || !_kickPending)
        {
            return;
        }

        _kickPending = false;

        try
        {
            NebulaResumeEntry.Log("重连拒绝: 已把本机移出房间(回主菜单), 请等会议结束后重新进入服务器。");
            ReconnectPrompt.PluginExiting = true;
            try { AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame); }
            finally { ReconnectPrompt.PluginExiting = false; }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("重连拒绝: 退出房间失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>本机连续处于"已离开房间"状态的起始时刻(0 = 当前在房间里)。</summary>
    private static float _leftRoomSince;

    /// <summary>"已离开房间"要稳定多久才算数(秒)。重连过程中 GameState 会短暂闪成 NotJoined。</summary>
    private const float LeftRoomStableSeconds = 2f;

    /// <summary>
    ///     由 Entry.Tick 调用: 离开房间后复位拒绝标记与过渡状态。
    ///
    ///   ▸ 为什么必须复位 _done
    ///     如果服务端在下一次进房时没有发 kind=3(BeginAttempt 不会被调用),
    ///     残留的 _done 会让这次进房的过渡被跳过 —— 表现是没职业 + 大厅 UI。
    ///
    ///   ▸ 为什么要"稳定 2 秒 + 没有暂存状态包"
    ///     重连过程里 GameState 会短暂出现 NotJoined。如果在"过渡已完成、状态包还没应用"
    ///     之间被复位, 状态包会被永久挂起("职业/位置/任务全没了")。
    ///     所以只在【确实已回到主菜单(无船、无大厅、持续 2 秒)且没有待应用的状态包】时复位。
    /// </summary>
    internal static void TickLeaveRoomReset()
    {
        try
        {
            var client = AmongUsClient.Instance;
            var left = client != null
                       && client.GameState == InnerNetClient.GameStates.NotJoined
                       && ShipStatus.Instance == null
                       && LobbyBehaviour.Instance == null;

            if (!left)
            {
                _leftRoomSince = 0f;
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            if (_leftRoomSince <= 0f)
            {
                _leftRoomSince = now;
                return;
            }

            if (now - _leftRoomSince < LeftRoomStableSeconds)
            {
                return;
            }

            if (RefusedByMeeting)
            {
                NebulaResumeEntry.Log("已离开房间 → 重连拒绝标记清除。");
                ClearRefusal();
            }

            // 离开房间后 NoS 的对局对象随之销毁: "本机 NoS 已开局"的标记必须一起清掉。
            //   原来只在过渡做过/挂起时才复位, 被会议拒绝而离开时它残留为 true ——
            //   下次进房重建 NoS 时就跳过了"切到对局阶段 + OnGameStart", NoS 只开了一半:
            //   失效玩家清扫不工作, 有人离场后每帧读已销毁的身体(日志刷 SEHException)。
            if (_nosStarted)
            {
                NebulaResumeEntry.Log("已离开房间 → 复位本机 NoS 开局标记。");
                _nosStarted = false;
            }

            if ((_done || _pendingRun) && !NebulaResumeEntry.HasPendingState)
            {
                NebulaResumeEntry.Log("已离开房间 → 复位过渡状态(下一次进房会重新执行过渡)。");
                Reset();
            }
        }
        catch { }
    }

    /// <summary>
    ///     一次新的"续局尝试"开始(收到 kind=3 时调用)。
    ///
    ///   作用: 把上一次尝试留下的 _done/_pendingRun 清掉, 保证这一次一定会
    ///   重新执行完整过渡。这就是原来"被踢回主菜单后再重连, 过渡被静默跳过"
    ///   那个问题的正确修法 —— 用"新信号"来复位, 而不是用"房间状态变化"来复位。
    /// </summary>
    internal static void BeginAttempt()
    {
        if (_done || _pendingRun)
        {
            NebulaResumeEntry.Log("续局过渡: 收到新的续局信号 → 复位上一次的过渡状态, 重新执行。");
        }

        _done = false;
        EndPending();
        _requestedAt = 0f;
        _lastTryAt = 0f;
        _hideLogged.Clear();
        _hideUiAt = 0f;
        _diagAt = 0f;
        ClearRefusal();
    }

    private static float _hideUiAt;
    private static float _diagAt;
    private static readonly System.Collections.Generic.HashSet<string> _hideLogged =
        new System.Collections.Generic.HashSet<string>();

    /// <summary>
    ///     本次过渡是【观战者(新玩家中途加入)】发起的, 而不是重连者发起的。
    ///
    ///   ▸ 为什么必须分开
    ///     "会议期间禁止重连"只针对【重连者】: 重连者要恢复原来的职业/生死/位置,
    ///     那套操作会往已经搭好的会议结构里插人, 所以必须拒。
    ///     而观战者是全新的旁观身份, 不需要恢复任何状态 —— 它走的是另一条路,
    ///     会议期间也应该照常把对局环境搭起来让他能看。
    ///     把两者混在一起判, 结果就是观战者在会议期间被当成"重连被拒"直接掐掉,
    ///     既装不上职业、也固定不了灵魂。
    ///
    ///   ⚠️ 这个值只属于【当前挂起的这一次请求】: 每次 Run() 都会重新赋值,
    ///      请求结束(执行 / 被拒 / 复位)时由 EndPending() 清掉。
    ///      早先是"置真后只有过渡跑完才复位"的粘性标记, 请求被挂起或被拒时会残留,
    ///      导致之后的重连者跳过会议拒绝。
    /// </summary>
    private static bool _forSpectator;

    /// <summary>
    ///     本机确实已离开房间: 未加入 + 无船 + 无大厅, 并且持续了 LeftRoomStableSeconds
    ///     (由 TickLeaveRoomReset 每帧记时; 重连途中短暂的 NotJoined 不算)。
    /// </summary>
    private static bool LeftRoom()
    {
        try
        {
            var client = AmongUsClient.Instance;
            return client != null
                   && client.GameState == InnerNetClient.GameStates.NotJoined
                   && ShipStatus.Instance == null
                   && LobbyBehaviour.Instance == null
                   && GameData.Instance == null
                   && _leftRoomSince > 0f
                   && UnityEngine.Time.unscaledTime - _leftRoomSince >= LeftRoomStableSeconds;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>结束当前挂起的请求(连同它的观战标记)。</summary>
    private static void EndPending()
    {
        _pendingRun = false;
        _forSpectator = false;
    }

    /// <summary>
    ///     请求执行续局过渡。幂等: 同一个对局只做一次。
    ///
    ///   ⚠️ 这里会挂起: 会议状态要靠房主的周期广播才能知道(见 MeetingFlag),
    ///      而续局信号是一进来就到的。所以请求先记账, 拿不到会议状态时
    ///      由 <see cref="TickDeferredRun"/> 继续推进, 最多等 WaitForMeetingInfo 秒。
    /// </summary>
    /// <param name="forSpectator">
    ///     由观战席(SpectatorSeat)调用时传 true: 会议期间不禁, 也不触发"拒绝+踢出"。
    /// </param>
    internal static void Run(bool forSpectator = false)
    {
        // 每次请求都无条件赋值(观战席本身也会核对 AmSpectator, 不靠这个标记残留)。
        _forSpectator = forSpectator || SpectatorSeat.AmSpectator;

        if (!_pendingRun)
        {
            _pendingRun = true;
            _requestedAt = UnityEngine.Time.unscaledTime;
        }

        RunNow();
    }

    /// <summary>由 Entry.Tick 每帧调用: 推进被挂起的过渡请求。</summary>
    internal static void TickDeferredRun()
    {
        if (!_pendingRun)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now - _lastTryAt < RetryInterval)
        {
            return;
        }

        RunNow();
    }

    private static void RunNow()
    {
        _lastTryAt = UnityEngine.Time.unscaledTime;

        // ── 幂等判断不能只看 _done ──
        //
        //   只按 _done 判断会出问题: 上一次对局结束后标志没有复位,
        //   下一次重连就会被直接跳过 —— 表现是
        //     GameState 停在 Joined、没有职业、没有 HUD、没有任何过渡日志。
        //
        //   所以这里同时检查"现在是否真的已经在对局中":
        //     真的在对局里 → 跳过(避免重复过渡)
        //     已经不在对局里 → _done 是残留标志, 复位后重做
        var clientNow = AmongUsClient.Instance;
        var inGameNow = false;

        try
        {
            inGameNow = clientNow != null
                && clientNow.GameState == InnerNetClient.GameStates.Started
                && ShipStatus.Instance != null
                && LobbyBehaviour.Instance == null;
        }
        catch { }

        if (_done && inGameNow)
        {
            EndPending();
            return;
        }

        if (_done && !inGameNow)
        {
            NebulaResumeEntry.Log("续局过渡: 检测到当前不在对局中(上次的对局已结束) → 复位后重新执行。");
            Reset();
        }

        // ── 会议 / 放逐动画进行中 ──
        //
        //   ⚠️ 不能用 `MeetingHud.Instance != null` 单独判断:
        //      会议界面是【会议开始那一刻】各客户端按 StartMeeting 自己建的,
        //      中途重连回来的人没收到那条 RPC, 本机根本没有 MeetingHud ——
        //      只查本机会永远判成"不在会议中"(这正是早先失效的原因)。
        //      真正的会议状态由房主周期广播, 见 MeetingFlag。
        MeetingFlag.NoteLocal();

        // 会议(含放逐动画)进行中 → 一律拒绝【重连者】本次重连。
        //
        //   不在会议期间恢复的理由: 会议界面是会议开始那一刻按当时在场玩家
        //   一次性搭好的定长结构(投票格数量、座位顺序、放逐动画参与者)。
        //   中途插进来的人在这套结构里没有位置, 塞进去只会得到半初始化的界面,
        //   而且不会自愈 —— 日志里就是 PopulateButtons → SortVotingArea
        //   → PlayerVoteArea.SetCosmetics 空引用, MeetingHud.Start 中断。
        //
        //   ⚠️ 观战者例外: 观战是全新身份, 不需要恢复任何状态, 会议期间照常入场。
        //      否则新玩家会被误判成"重连被拒"而直接掐掉(职业装不上、灵魂固定不了)。
        // 房主支持会议中进入: 重连者和观战者都不拒绝, 界面切换原地等到散会
        //   (会议画面由 MeetingDefer 负责; 观战者的职业/灵魂由 SpectatorSeat 当场装好, 不等这里)。
        if (MeetingFlag.HostSupportsMeetingJoin && (ExileController.Instance != null || MeetingFlag.Active))
        {
            return;
        }

        if (!_forSpectator && (ExileController.Instance != null || MeetingFlag.Active))
        {

            // ★ 先挂起等待, 不要立刻判死。
            //   已观察到: 会议刚结束那几秒里, 房主的会议状态广播可能还是 1,
            //   而本机其实已经没有会议界面了 —— 这时直接拒绝会把人踢出去,
            //   于是他重新进服又被拒, 反复几次后就停在"没职业 + 大厅 UI"。
            //   规则: 本机看不到会议界面时, 最多等 10 秒等房主刷成 0;
            //   真正的会议(本机有会议界面 / 放逐动画)才立即拒绝。
            var localMeetingUi = MeetingHud.Instance != null || ExileController.Instance != null;

            if (!localMeetingUi
                && UnityEngine.Time.unscaledTime - _requestedAt < MeetingRefuseGraceSeconds)
            {
                return;   // 继续挂起, 下一轮再试
            }

            RefuseByMeeting(localMeetingUi
                ? (ExileController.Instance != null ? "放逐动画" : "会议进行中")
                : "会议状态未及时刷新");
            return;
        }

        if (RefusedByMeeting)
        {
            EndPending();
            return;
        }

        // 还没有会议状态 → 先等一下(最多 WaitForMeetingInfo 秒), 别急着把界面切过去。
        if (!MeetingFlag.HasFreshInfo)
        {
            if (UnityEngine.Time.unscaledTime - _requestedAt < WaitForMeetingInfo)
            {
                return;   // 挂起, 下一轮再试
            }

            NebulaResumeEntry.Log("续局过渡: " + (int)WaitForMeetingInfo
                + " 秒内没收到房主的会议状态广播 → 按'不在会议中'继续。");
        }

        // ── 等本机角色就位 ──
        //   续局信号有时比本机角色先到(实测: 同一套流程, 角色已在场时一切正常;
        //   角色还没生成时 HUD、NoS 开局全部空引用, 恢复包无处可落 →
        //   没职业 + 大厅 UI 退不掉)。所以先等角色、资料、原版职业都到齐再切。
        if (!LocalBodyReady(out var notReady))
        {
            var nowWait = UnityEngine.Time.unscaledTime;
            if (nowWait - _requestedAt < WaitForLocalBody)
            {
                if (nowWait >= _waitLogAt)
                {
                    _waitLogAt = nowWait + 2f;
                    NebulaResumeEntry.Log("续局过渡: 等待本机角色就位(" + notReady + ")。");
                }

                return;   // 挂起, 下一轮再试
            }

            // 等了这么久还没角色, 而且本机已经回到主菜单(被踢 / 断线) → 放弃这次过渡。
            //   否则会在主菜单里照样"开局", 把 HUD 打开后以 0 名玩家重建, 每帧刷 HudManager 空引用。
            //   ⚠️ 只在等待超时这一处判断: 刚进房时本机状态也可能短暂像"不在房间", 不能一开始就判。
            if (LeftRoom())
            {
                EndPending();
                _done = true;
                NebulaResumeEntry.Log("续局过渡: 等待超时且本机已回到主菜单(被踢或断线) → 放弃这次过渡。");
                return;
            }

            NebulaResumeEntry.Log("续局过渡: 等待本机角色超过 " + (int)WaitForLocalBody
                + " 秒(" + notReady + ") → 仍然继续, 后续步骤会自动补做。");
        }

        EndPending();
        _done = true;

        NebulaResumeEntry.Log("续局过渡: 开始。");

        var client = AmongUsClient.Instance;
        if (client == null)
        {
            NebulaResumeEntry.Log("续局过渡: AmongUsClient 为空, 放弃。");
            return;
        }

        // ── ① 设为已开始 ──
        // 注意: GameState 的类型是嵌套枚举 InnerNetClient.GameStates,
        //       不是顶层的 InnerGameStates(两者同名易混)。
        try
        {
            client.GameState = InnerNetClient.GameStates.Started;
            NebulaResumeEntry.Log("续局过渡 1/5: GameState = Started。");
        }
        catch (System.Exception e) { NebulaResumeEntry.Log("续局过渡 1/5 失败: " + e.Message); }

        // ── ② 隐藏大厅(SetActive(false) 比 Destroy 稳) ──
        try
        {
            var lobby = LobbyBehaviour.Instance;
            if (lobby != null)
            {
                lobby.gameObject.SetActive(false);
                NebulaResumeEntry.Log("续局过渡 2/5: 已隐藏 LobbyBehaviour。");
            }
        }
        catch (System.Exception e) { NebulaResumeEntry.Log("续局过渡 2/5 失败: " + e.Message); }

        // ── ③ 隐藏大厅 UI(房间号 / 设置 / 开始按钮) ──
        try
        {
            var gsm = GameStartManager.Instance;
            if (gsm != null)
            {
                gsm.gameObject.SetActive(false);
                NebulaResumeEntry.Log("续局过渡 3/5: 已隐藏 GameStartManager。");
            }
        }
        catch (System.Exception e) { NebulaResumeEntry.Log("续局过渡 3/5 失败: " + e.Message); }

        // ── ④ 打开游戏内 HUD ──
        try
        {
            var hud = HudManager.Instance;
            if (hud != null)
            {
                hud.SetHudActive(true);
                NebulaResumeEntry.Log("续局过渡 4/5: SetHudActive(true)。");
            }
        }
        catch (System.Exception e) { NebulaResumeEntry.Log("续局过渡 4/5 失败: " + Describe(e)); }

        // ── ⑤ 重建本机 NoS 层 ──
        RebuildLocalNoS();

        // ── ⑥ 全房同步: 替本机补交可能错过的回执 ──
        //   见 SyncAssist: 中途进来的人错过了正在进行的同步轮次, 别人会一直等他。
        try { SyncAssist.OnEnteredMidRound(); } catch { }

        // ── ⑦ 装扮缓存: 本机没经历开局介绍, 船上的装扮缓存是空的 ──
        //   不补的话, 之后会议里所有人的投票格都只剩名字(见 CosmeticsWarmup)。
        try { CosmeticsWarmup.OnEnteredMidRound(); } catch { }

        // ── ⑧ 全场职业: 本机只给其他人装了占位船员, 向房主要每个人当前的职业 ──
        //   不补的话, 在本机眼里全场都是船员(见 RoleSync)。
        try { RoleSync.RequestAfterTransition(); } catch { }

        // 提示文案不要写死成"灵魂状态" —— 重连后可能是活人也可能是灵魂,
        // 由状态包决定。这里只确认"回到对局"这一件事。
        ShowPopup("已回到对局");
    }

    /// <summary>
    ///     把全场玩家注册进本机 NoS, 并把 NoS 阶段切到 Initialized。
    /// </summary>
    /// <remarks>
    ///     ⚠️ 必须走反射: NebulaGameManager 是 NOS 的 internal 类型,
    ///       没有公开入口。这里查的方法名一局内固定, 所以缓存起来避免重复扫描。
    /// </remarks>
    private static void RebuildLocalNoS()
    {
        try
        {
            var ngmType = FindType("Nebula.Game.NebulaGameManager");
            if (ngmType == null)
            {
                NebulaResumeEntry.Log("重建: 找不到 NebulaGameManager 类型。");
                return;
            }

            // Instance 是静态属性
            var instProp = ngmType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var ngm = instProp?.GetValue(null);

            if (ngm == null)
            {
                // 这是整套重连最关键的失败点: NoS 没为"中途进来的玩家"建游戏实例时,
                // 后面一切都无从谈起。
                NebulaResumeEntry.Log("重建【关键】: NebulaGameManager.Instance 为空 —— 本机在 NoS 层没有游戏实例。");
                return;
            }

            var register = ngmType.GetMethod("RegisterPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var all = PlayerControl.AllPlayerControls;
            var reg = 0;
            var rolesSet = 0;

            // ★ 注册之后必须给每个 NoS 玩家装一个【有效的占位职业】。
            //
            //   已知问题: 只注册不装职业时, 模组的每帧 HUD 更新会崩:
            //       Virial.Game.Player.get_IsImpostor() NullReferenceException
            //         at HudManagerExtension.UpdateHudContent
            //   连带后果: HUD 刷不出来、紧急会议按钮失效、大厅 UI 退不掉。
            //
            //   占位职业只为让 NoS 的玩家对象完整(必须有 Role 才能被读);
            //   真正的身份由随后下发的状态包覆盖(职业/幽灵/附加/生死)。
            var crewRole = PlaceholderCrewmate();

            if (all != null && register != null)
            {
                for (var i = 0; i < all.Count; i++)
                {
                    var pc = all[i];
                    if (pc == null)
                    {
                        continue;
                    }

                    // ⚠️ 资料还没同步过来的玩家【不能注册】。
                    //   已观察到: 注册了这种半初始化的对象之后, NOS 的 OnGameStart
                    //   会在成就模块里对着 null 玩家调用 HasTag() 直接抛空引用
                    //   (AchievementManagerModule.OnGameStart → HasTag(null)),
                    //   整个 OnGameStart 就此中断 —— 结果是 NoS 半初始化,
                    //   表现就是"职业显示 Unassigned、大厅 UI 退不掉"。
                    //   宁可少注册一个, 也不要喂进去坏数据; 下一轮对账会补上。
                    if (pc.Data == null)
                    {
                        NebulaResumeEntry.Log("重建: 跳过资料未就绪的玩家(id=" + pc.PlayerId + "), 下轮再补。");
                        continue;
                    }

                    try
                    {
                        var nosP = register.Invoke(ngm, new object[] { pc });
                        reg++;

                        if (crewRole != null && nosP != null)
                        {
                            try
                            {
                                LocalSetRole(nosP, crewRole);
                                rolesSet++;
                            }
                            catch (System.Exception se)
                            {
                                NebulaResumeEntry.Log("重建: 装占位职业(id=" + pc.PlayerId + ") 失败: " + se.Message);
                            }
                        }
                    }
                    catch (System.Exception re)
                    {
                        NebulaResumeEntry.Log("重建: RegisterPlayer(id=" + pc.PlayerId + ") 失败: " + re.Message);
                    }
                }
            }

            NebulaResumeEntry.Log("重建: 已注册 " + reg + " 名玩家进本机 NoS, 装占位职业 " + rolesSet + " 人。");

            // ── 确认本机玩家一定在 NoS 里 ──
            //   观战者的收尾只认"NoS 里有本机玩家"; 这一步没做成, 后面全部免谈
            //   (日志会一直刷"收尾等待中 —— 本机还没被注册进 NoS")。
            try
            {
                var lpReg = PlayerControl.LocalPlayer;
                if (lpReg != null && register != null
                    && Virial.Game.Player.GetPlayer(lpReg.PlayerId) == null)
                {
                    register.Invoke(ngm, new object[] { lpReg });
                    NebulaResumeEntry.Log("重建: 本机玩家单独补注册(id=" + lpReg.PlayerId + ")。");
                }
            }
            catch (System.Exception le)
            {
                NebulaResumeEntry.Log("重建: 本机玩家补注册失败: " + le.Message);
            }

            // ── 切 NoS 游戏阶段 ──
            // GameState 不是 Initialized 时, HUD 会停在大厅态、职业显示 unassigned。
            // 只做一次: 会议中进入时会议前已经做过, 散会后的正式续局不能再调 OnGameStart。
            // 本机玩家必须已经在 NoS 里: OnGameStart 会读本机玩家的职业与外观,
            //   读到空就中途抛出, NoS 停在半初始化(没职业、HUD 不切)且不能再调第二次。
            //   本机还不在时先不切, 由 EnsureLocalNoS 在角色到达后补做。
            if (!_nosStarted && !LocalInNoS())
            {
                NebulaResumeEntry.Log("重建: 本机玩家还不在 NoS 里 → 暂不切对局阶段, 角色到达后补做。");
            }
            else if (!_nosStarted)
            try
            {
                _nosStarted = true;
                var ngsType = FindType("Nebula.Game.NebulaGameStates");
                var setter = ngmType.GetProperty("GameState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);

                if (ngsType != null && setter != null)
                {
                    // Initialized 在 NebulaGameStates 里是第 2 个值(从 0 数)
                    setter.Invoke(ngm, new object[] { System.Enum.ToObject(ngsType, 1) });
                    NebulaResumeEntry.Log("重建: 已设 NoS GameState = Initialized。");
                }

                ngmType.GetMethod("OnGameStart",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(ngm, null);

                NebulaResumeEntry.Log("重建: 已调用 OnGameStart()。");
            }
            catch (System.Exception pe)
            {
                NebulaResumeEntry.Log("重建: NoS 阶段切换异常: " + Describe(pe));
            }

            // ── 地图已加载时, 用带角色的重载把 HUD 切到游戏态 ──
            try
            {
                var lp = PlayerControl.LocalPlayer;
                var hud = HudManager.Instance;

                // 会议进行中不切 HUD(会议界面盖在上面, 散会后的正式续局会再切一次)。
                if (ShipStatus.Instance != null && hud != null && lp != null
                    && lp.Data != null && lp.Data.Role != null
                    && MeetingHud.Instance == null && ExileController.Instance == null)
                {
                    hud.SetHudActive(lp, lp.Data.Role, true);
                    NebulaResumeEntry.Log("重建: 已 SetHudActive(LocalPlayer, Role, true)。");
                }
            }
            catch (System.Exception he)
            {
                NebulaResumeEntry.Log("重建: 游戏 HUD 重载失败: " + he.Message);
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("重建异常: " + e.GetType().Name + " : " + e.Message);
        }
    }

    // ── 占位职业与本地装职业 ────────────────────────────────

    /// <summary>
    ///     取 NOS 的船员定义(Nebula.Roles.Crewmate.Crewmate.MyRole)。
    ///     仅作为占位, 让 NoS 玩家对象完整可用。
    /// </summary>
    private static object? PlaceholderCrewmate()
    {
        try
        {
            var t = FindType("Nebula.Roles.Crewmate.Crewmate");
            if (t == null)
            {
                return null;
            }

            var f = t.GetField("MyRole", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     在【本机】给某个 NoS 玩家装职业, 不广播。
    ///     走 NOS 自己的 RpcInvokerSetRole(...).InvokeLocal() —— 这是本地执行入口。
    /// </summary>
    private static void LocalSetRole(object nosPlayer, object definedRole)
    {
        if (nosPlayer == null || definedRole == null)
        {
            return;
        }

        var m = nosPlayer.GetType().GetMethod(
            "RpcInvokerSetRole",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(Virial.Assignable.DefinedRole), typeof(int[]), typeof(Virial.Assignable.RoleAssignType) },
            null);

        if (m == null)
        {
            return;
        }

        var invoker = m.Invoke(nosPlayer, new object[]
        {
            definedRole,
            System.Array.Empty<int>(),
            Virial.Assignable.RoleAssignType.Standard,
        });

        invoker?.GetType()
            .GetMethod("InvokeLocal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(invoker, null);

        // 占位船员只为让 NoS 对象完整, 记下这个职业实例: 结算总结里不显示它。
        if (ReferenceEquals(definedRole, PlaceholderCrewmate()))
        {
            try { EndSummary.NotePlaceholder((nosPlayer as Virial.Game.Player)?.Role); } catch { }
        }
    }

    /// <summary>屏幕上弹一条提示(游戏原生的弹出式提示)。</summary>
    internal static void ShowPopup(string text)
    {
        try
        {
            var hud = HudManager.Instance;
            if (hud != null)
            {
                hud.ShowPopUp(text);
            }
        }
        catch { }
    }

    /// <summary>
    ///     持续隐藏大厅 UI。
    ///
    ///   为什么不能只隐藏一次: 玩家重连的整个过程里, 大厅相关的对象可能被
    ///   重新创建 / 重新激活(场景切换、GameData 重建、OnGameStart 等)。
    ///   一次隐藏不足以覆盖全部时序, 因此改为对局期间持续兜住。
    ///   每 0.5 秒检查一次, 只在对象存在且仍处于激活状态时才动它。
    /// </summary>
    internal static void TickHideLobbyUi()
    {
        // 只跟着过渡状态机走: 过渡已完成, 或者有一个确定会执行的挂起请求。
        //   早先这里还有一条"中途加入者直接放行"的旁路, 它和环境兜底
        //   (Entry.TickEnvironmentFallback)是两套并行触发 —— 会出现
        //   "大厅 UI 藏掉了, 职业却没装"。现在"没收到续局信号"的情况
        //   统一由环境兜底去调 Run(), 这里不再自己判断。
        // 会议中进来的人: 续局要等散会, 但会议期间大厅界面(等待主持人/房间信息)也不该露出来。
        if (RefusedByMeeting || (!_done && !_pendingRun && !MeetingDefer.Engaged))
        {
            return;
        }

        if (ShipStatus.Instance == null)
        {
            return;   // 已经不在对局场景里了
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _hideUiAt)
        {
            return;
        }

        _hideUiAt = now + 0.5f;

        try
        {
            var gsm = GameStartManager.Instance;
            HideObject("GameStartManager", gsm != null ? gsm.gameObject : null);
        }
        catch { }

        try
        {
            var lb = LobbyBehaviour.Instance;
            HideObject("LobbyBehaviour", lb != null ? lb.gameObject : null);
        }
        catch { }

        try
        {
            var pane = UnityEngine.Object.FindObjectOfType<LobbyInfoPane>();
            if (pane != null)
            {
                HideObject("LobbyInfoPane", pane.gameObject);
            }
        }
        catch { }
    }

    // ── 供生成守卫复用的三个助手 ──
    internal static System.Type? Find(string name) => FindType(name);
    internal static object? CrewRole() => PlaceholderCrewmate();
    internal static void SetRoleLocal(object nosPlayer, object definedRole) => LocalSetRole(nosPlayer, definedRole);

    private static void HideObject(string label, UnityEngine.GameObject? go)
    {
        if (go == null)
        {
            return;
        }

        try
        {
            if (!go.activeSelf)
            {
                return;
            }

            go.SetActive(false);

            if (_hideLogged.Add(label))
            {
                NebulaResumeEntry.Log("大厅UI: 已隐藏 " + label + "。");
            }
        }
        catch { }
    }

    /// <summary>
    ///     每 5 秒打印一次本机 GameData 的玩家资料概况。
    ///     用来诊断"会议里全是没信息的小绿人"这类问题 —— 如果资料缺失,
    ///     日志里会直接看到 name=? / color=0 或者条目数为 0。
    /// </summary>
    internal static void TickDataDiagnostics()
    {
        if (!_done)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _diagAt)
        {
            return;
        }

        _diagAt = now + 5f;

        try
        {
            var gd = GameData.Instance;
            if (gd == null)
            {
                return;
            }

            var all = gd.AllPlayers;
            var sb = new System.Text.StringBuilder();
            sb.Append("GameData 诊断: 条目数=").Append(all == null ? -1 : all.Count).Append(" :: ");

            if (all != null)
            {
                var shown = 0;
                for (var i = 0; i < all.Count && shown < 8; i++)
                {
                    var info = all[i];
                    if (info == null)
                    {
                        sb.Append("[null] ");
                        shown++;
                        continue;
                    }

                    var dead = false;
                    try { dead = info.IsDead; } catch { }

                    // 外观诊断: NetworkedPlayerInfo 上的外观是【字段】不是属性,
                    //   名字可能是 DefaultOutfit / CurrentOutfit / outfit 之一。
                    //   只用 GetProperty 取会一直是 null(诊断信息失真)。
                    // 外观诊断: PlayerInfo 上的外观在 Outfits 字典里,
                    // 键是 PlayerOutfitType(Default 等), 值是 PlayerOutfit。
                    var outfitInfo = "none";
                    try
                    {
                        var op = info.GetType().GetProperty("Outfits",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var outfits = op?.GetValue(info);

                        if (outfits == null)
                        {
                            outfitInfo = "outfits=null";
                        }
                        else
                        {
                            var args = outfits.GetType().GetGenericArguments();
                            if (args.Length == 2)
                            {
                                var key = System.Enum.Parse(args[0], "Default");
                                var item = outfits.GetType().GetProperty("Item")
                                    ?.GetValue(outfits, new[] { key });

                                if (item == null)
                                {
                                    outfitInfo = "Default=null";
                                }
                                else
                                {
                                    var t2 = item.GetType();
                                    var col = t2.GetProperty("ColorId");
                                    var hat = t2.GetProperty("HatId");
                                    var cv = col?.GetValue(item);
                                    var hv = hat?.GetValue(item) as string;
                                    outfitInfo = "color=" + (cv?.ToString() ?? "?")
                                               + ",hat=" + (string.IsNullOrEmpty(hv) ? "-" : "Y");
                                }
                            }
                        }
                    }
                    catch (System.Exception oe)
                    {
                        outfitInfo = "err:" + oe.GetType().Name;
                    }

                    sb.Append("{id=").Append(info.PlayerId)
                        .Append(",client=").Append(info.ClientId)
                        .Append(",name=").Append(string.IsNullOrEmpty(info.PlayerName) ? "?" : info.PlayerName)
                        .Append(",dead=").Append(dead)
                        .Append(",").Append(outfitInfo)
                        .Append("} ");
                    shown++;
                }
            }

            NebulaResumeEntry.Log(sb.ToString());
        }
        catch { }
    }

    /// <summary>
    ///     按名字找类型。先查已加载程序集, 避免每次都全程序集扫描。
    /// </summary>
    private static System.Type? FindType(string name)
    {
        try
        {
            var t = System.Type.GetType(name, false);
            if (t != null)
            {
                return t;
            }

            var asms = System.AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < asms.Length; i++)
            {
                try
                {
                    t = asms[i].GetType(name, false);
                    if (t != null)
                    {
                        return t;
                    }
                }
                catch { }
            }
        }
        catch { }

        return null;
    }
}
