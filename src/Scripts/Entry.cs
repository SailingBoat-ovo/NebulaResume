// ============================================================
// Entry.cs — NebulaResume 插件入口  v2.0
//
// 本插件是【Fmpostor 服务端断线重连功能】的客户端衔接层。
// 它不做任何判定: 谁可以重连、保槽多久、要不要恢复, 全部由服务端决定。
// 它只做两件事:
//   ① 采集: 把本局每个玩家的 NOS 状态编码成 int[], 交给服务端
//   ② 恢复: 收到服务端下发的状态包, 在本地还原
//
// ▸ 通信通道(协议见 README)
//     · 客户端发送: 走 NOS 的 RPC 通道 (香草 RpcCalls = 128)
//     · 服务端发送: Fmpostor 的 IGame.StartRpc(targetNetId, callId, targetClientId)
//                   + FinishRpcAsync —— 定向单播, 可精确发给重连的那条连接
//   ▸ 有意不用顶层自定义 flag: 那条路径在客户端要额外打 Harmony 补丁,
//     与其他模组的补丁存在冲突风险。RPC 通道不需要任何补丁。
//
// ▸ 为什么本插件必须存在
//   NOS 的模组状态是"每客户端各持一份"的, 服务端看不到(它只看到香草职业枚举)。
//   只有同版本的 NOS 客户端能解释 NOS 状态, 所以采集与还原只能在这里做。
//   服务端只当不透明容器, 因此 NOS 升级后服务端不需要改。
// ============================================================

namespace NebulaResume;

[NebulaPreprocess(PreprocessPhase.Roles)]
public class NebulaResumeEntry
{
    /// <summary>
    ///     上报周期(毫秒)。
    ///
    ///     ⚠️ 这个值直接决定【掉线时最多丢失多久的状态】:
    ///       期间玩家若发生职业变更(招募/侧kick/薛定谔的猫变形), 而他在下一次
    ///       上报之前掉线, 那变更就丢了 —— 恢复出来的是【变更前】的职业。
    ///     取 1 秒: 一局 12 人 × 每秒 1 条 × 约 20 字节, 流量可忽略;
    ///     而把丢失窗口从 3 秒压到 1 秒。
    ///
    ///     ⚠️ 无法做到"零丢失": 上报是周期性采样, 而硬断线(拔网线/崩溃)时客户端
    ///       已经没有机会再发任何东西。要彻底消除只能让服务端参与模组协议本身,
    ///       那会把服务端绑死在模组版本上, 是更差的取舍。
    /// </summary>
    private const int HeartbeatMs = 1000;

    internal static void Log(string msg)
    {
        try
        {
            NebulaAPI.Logging.BepInExLogger().Message("[NebulaResume] " + msg);
        }
        catch { }
    }

    public static void Preprocess(NebulaPreprocessor preprocessor)
    {
        Log("===== NebulaResume loaded (v2.0) =====");
        Log("采集/恢复: 主职业 + 幽灵职业 + 附加职业 + 生死");

        // 用 MonoBehaviour 驱动采集。
        // 【为什么不用 NebulaAPI.RegisterExtension<T, GameUpdateEvent>】
        //   实机验证过: 注册调用成功返回, 但回调一次都没被触发(日志中心跳行完全缺失)。
        //   MonoBehaviour 是本机已被验证可行的方式 —— 同一个游戏里
//   (此处原有一句外部模块名的说明, 已移除)
        ResumeDriver.Install();

        // 注册收发端点(构造 RemoteProcess 会把哈希注册进 AllNebulaProcess,
        // 服务端用同一个哈希发包即可派发到本插件)。
        try
        {
            NebulaStateWire.EnsureReady();
            Log("传输端点已注册");
        }
        catch (System.Exception e)
        {
            Log("传输端点注册失败: " + e.GetType().Name + " : " + e.Message);
        }

        // 掉线后的"是否重连"询问。
        // 用【独立补丁】而不是 PatchAll: 本插件若某个补丁目标缺失, PatchAll 会整体中断,
        // 把其它功能一起带走(这条约定是从 NOS 自带 addon 的注释里学到的)。
        try
        {
            var harmony = new HarmonyLib.Harmony("NebulaResume");
            ReconnectPrompt.Install(harmony);
            FrameGuard.Install(harmony);
            OutfitSync.Install(harmony);
            MeetingSeat.Install(harmony);
            MeetingDefer.Install(harmony);
            MeetingProxy.Install(harmony);
            EndSummary.Install(harmony);
        }
        catch (System.Exception e)
        {
            Log("掉线询问安装失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>
    ///     当前是否【真的】在某局游戏里。
    ///
    /// ⚠️ 不能用 `GameState == Started` 单独判定, 原因如下:
    ///   重连过程中 GameState 会先变成 Started, 而玩家列表还是空的
    ///   (本地角色尚未 spawn / 玩家信息还没同步过来)。
    ///   用这个宽松条件会让重连被过早判定成"成功", 状态机提前复位。
    ///   所以额外要求: 本地玩家存在 且 玩家列表非空。
    /// </summary>
    internal static bool IsInGame()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null)
            {
                return false;
            }

            if (client.GameState != InnerNetClient.GameStates.Started)
            {
                return false;
            }

            if (PlayerControl.LocalPlayer == null)
            {
                return false;
            }

            return Player.AllPlayers.Count() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前是否在主菜单(用于冷启动询问)。</summary>
    internal static bool IsInMainMenu()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null)
            {
                return false;
            }

            return client.GameState == InnerNetClient.GameStates.NotJoined;
        }
        catch
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════
    // 传输: 待接线的两个端点
    // ══════════════════════════════════════════════════════════

    /// <summary>
    ///     上报某个玩家的 NOS 状态。
    ///     服务端从连接本身取得 ClientId(不信客户端自报), 因此这里只带 playerId。
    /// </summary>
    public static void ReportNebulaState(int playerId, int[] chunks)
    {
        try
        {
            // 帧格式: [KindReport, playerId, ...stateChunks]
            //   stateChunks: NebulaStateCodec.Encode 的输出, 服务端不解析其内容。
            //   服务端靠"长度与声明值精确相符"来识别这是不是我们的包, 因此不需要
            //   客户端额外携带任何标识。
            var frame = new int[2 + chunks.Length];
            frame[0] = NebulaStateWire.KindReport;
            frame[1] = playerId;
            for (var i = 0; i < chunks.Length; i++)
            {
                frame[2 + i] = chunks[i];
            }
            NebulaStateWire.Send(frame);

            _sentTotal++;
            if (chunks.Length > _maxChunkLen) { _maxChunkLen = chunks.Length; }
            if (chunks.Length < _minChunkLen) { _minChunkLen = chunks.Length; }
        }
        catch (System.Exception e)
        {
            Log("上报失败 playerId=" + playerId + " : " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>
    ///     服务端在重连成功后定向下发状态包, 由本机应用。
    /// </summary>
    /// <remarks>
    ///     由 NebulaStateWire 在收到服务端下发的恢复包时调用。
    /// </remarks>
    // 暂存的状态包: 会议进行中 / 过渡尚未完成时先收下, 条件满足后自动应用。
    private static bool _hasPendingState;
    private static int _pendingPlayerId;
    private static int[] _pendingChunks;

    /// <summary>是否有暂存、尚未应用的状态包(离开房间复位时要避开这个窗口)。</summary>
    internal static bool HasPendingState => _hasPendingState;

    /// <summary>恢复包因本机 NoS 玩家未就绪而重试的截止时刻(0 = 不在重试)。</summary>
    private static float _applyRetryUntil;

    public static void ApplyNebulaState(int playerId, int[] chunks)
    {
        // ── 会议进行中进来的玩家: 拒绝本次重连 ──
        //   过渡还没做过 = 本机还没把对局环境搭起来 = 属于"中途插入"。
        //   这时候应用角色/生死只会落在半初始化的会议界面上, 所以直接丢弃状态包。
        //
        //   ⚠️ 会议状态取自 MeetingFlag(房主广播), 不是本机 MeetingHud ——
        //      中途重连回来的人本机根本没有会议界面。
        if (MeetingFlag.Active && !ResumeTransition.Done && !MeetingFlag.HostSupportsMeetingJoin)
        {
            ResumeTransition.RefuseByMeeting("恢复包");
            _hasPendingState = false;
            _pendingChunks = null;
            Log("状态包丢弃: 会议进行中 → 本次重连已取消。");
            return;
        }

        // 过渡未完成 / 会议中 → 暂缓应用。
        //   会议 UI 是按当时在场玩家一次性搭好的, 中途改角色状态会在半初始化的
        //   投票格上出问题; 过渡(NoS 重建等)没做完时应用状态也无处可落。
        if (!ResumeTransition.Done || MeetingFlag.Active)
        {
            // 兜底: 状态包到了但过渡没跑(例如漏收续局信号) → 主动补一次,
            // 否则这个包会被永久挂起, 表现就是"什么都没恢复"。
            if (!ResumeTransition.Done && !MeetingFlag.Active)
            {
                try { ResumeTransition.Run(); } catch { }
            }

            _hasPendingState = true;
            _pendingPlayerId = playerId;
            _pendingChunks = chunks;
            Log("状态包暂存: 过渡完成=" + ResumeTransition.Done
                + " 会议中=" + MeetingFlag.Active + " → 条件满足后自动应用。");
            return;
        }

        ApplyNow(playerId, chunks);
    }

    /// <summary>由 Entry.Tick 每帧调用: 条件满足后应用暂存的状态包。</summary>
    internal static void TickPendingState()
    {
        if (!_hasPendingState)
        {
            return;
        }

        // 本次进房已被拒绝(会议进行中) → 暂存的包一并作废, 不能等散会后再补。
        if (ResumeTransition.RefusedByMeeting)
        {
            _hasPendingState = false;
            _pendingChunks = null;
            Log("暂存状态包丢弃: 本次重连已取消(会议进行中)。");
            return;
        }

        if (!ResumeTransition.Done || MeetingFlag.Active)
        {
            return;
        }

        var pid = _pendingPlayerId;
        var chunks = _pendingChunks;
        _hasPendingState = false;
        _pendingChunks = null;
        if (_applyRetryUntil <= 0f)
        {
            Log("条件已满足, 应用暂存的状态包。");
        }
        ApplyNow(pid, chunks);
    }

    /// <summary>本机是否已经历过一次重连恢复(决定是否开启自身死亡态断言)。</summary>
    private static bool _isResumedPlayer;

    /// <summary>本机是否已经按【重连者】路径恢复过(供观战判定做互斥防御)。</summary>
    internal static bool HasResumedAsReconnecter => _isResumedPlayer;

    /// <summary>本次重连后是否为灵魂(掉线前已死)。供死亡态断言的开关判断。</summary>
    private static bool _resumedAsGhost;

    /// <summary>待应用的位置快照与执行时刻。</summary>
    private static NebulaStateCodec.Snapshot _pendingPosition;
    private static float _positionApplyAt;
    private static float _selfGhostAt;

    /// <summary>
    ///     持续保证"本机玩家是死的"。
    ///
    ///   为什么需要: 香草的 PlayerInfo 由服务端/房主序列化同步, 我们本地设的
    ///   IsDead=true 可能被下一次同步覆盖回 false —— 一旦被覆盖, 别人就又能看到
    ///   你是活人、能击杀你、会议里也不显示死亡标记。所以每 250ms 断言一次,
    ///   发现被改回活人就重新置死亡并 MarkDirty(归属方标脏才能广播出去)。
    /// </summary>
    internal static void TickSelfGhostAssert()
    {
        // 只有"重连后是灵魂"的玩家才需要防覆盖;
        // 活着回来的玩家如果也被强制置死, 那就是 bug, 不该由这里兜。
        if (!_isResumedPlayer || !_resumedAsGhost)
        {
            return;
        }

        if (ShipStatus.Instance == null)
        {
            _isResumedPlayer = false;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _selfGhostAt)
        {
            return;
        }

        _selfGhostAt = now + 0.25f;

        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.Data == null)
            {
                return;
            }

            if (!lp.Data.IsDead)
            {
                lp.Data.IsDead = true;
                lp.Data.MarkDirty();
                Log("自身死亡态被覆盖 → 已重新置死亡并广播(MarkDirty)。");
            }
        }
        catch { }
    }

    /// <summary>由 Entry.Tick 每帧调用: 到点后应用位置。</summary>
    internal static void TickPendingPosition()
    {
        TickPositionVerify();

        if (_positionApplyAt <= 0f)
        {
            return;
        }

        if (UnityEngine.Time.unscaledTime < _positionApplyAt)
        {
            return;
        }

        _positionApplyAt = 0f;

        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp != null)
            {
                _snapFrom = lp.transform.position;
            }

            if (NebulaStateCodec.ApplyPosition(lp, _pendingPosition))
            {
                _snapTarget = new UnityEngine.Vector2(_pendingPosition.PosX / 1000f, _pendingPosition.PosY / 1000f);
                _verifyStep = 0;
                _verifyAt = UnityEngine.Time.unscaledTime + VerifyDelays[0];
            }
        }
        catch (System.Exception e)
        {
            Log("位置应用异常: " + e.Message);
        }
    }

    // ── 位置复查 ──
    //   瞬移之后, 房主那边的身体生成/网络同步偶尔会把人拉回出生点(实测"位置错误")。
    //   瞬移后再查两次: 人离目标很远、却还停在瞬移前的地方 → 判定为被拉回, 再瞬移一次。
    //   玩家自己走开的情况不会误判(那时他已经离开了瞬移前的位置)。
    private static readonly float[] VerifyDelays = { 1.2f, 2.5f };
    private static int _verifyStep = -1;
    private static float _verifyAt;
    private static UnityEngine.Vector2 _snapTarget;
    private static UnityEngine.Vector2 _snapFrom;

    private static void TickPositionVerify()
    {
        if (_verifyStep < 0 || UnityEngine.Time.unscaledTime < _verifyAt)
        {
            return;
        }

        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || MeetingHud.Instance != null)
            {
                _verifyStep = -1;
                return;
            }

            UnityEngine.Vector2 cur = lp.transform.position;
            var offTarget = UnityEngine.Vector2.Distance(cur, _snapTarget) > 1.0f;
            var stillAtOld = UnityEngine.Vector2.Distance(cur, _snapFrom) < 0.8f;

            if (offTarget && stillAtOld)
            {
                NebulaStateCodec.ApplyPosition(lp, _pendingPosition);
                Log("位置复查: 被拉回原处 (" + cur.x.ToString("F2") + ", " + cur.y.ToString("F2") + ") → 已重新瞬移。");
            }
            else if (_verifyStep == 0)
            {
                Log("位置复查: 位置正常。");
            }
        }
        catch { }

        _verifyStep++;
        if (_verifyStep >= VerifyDelays.Length)
        {
            _verifyStep = -1;
            return;
        }

        _verifyAt = UnityEngine.Time.unscaledTime + VerifyDelays[_verifyStep] - VerifyDelays[_verifyStep - 1];
    }

    /// <summary>本局已经重连过几次(仅本机视角)。</summary>
    private static int _resumeCount;
    /// <summary>是否因为超过重连上限而拒绝本次恢复。</summary>
    internal static bool ResumeRejected { get; private set; }

    /// <summary>屏幕上弹一条提示。</summary>
    internal static void ShowPopup(string text)
    {
        try
        {
            if (HudManager.Instance != null)
            {
                HudManager.Instance.ShowPopUp(text);
            }
        }
        catch { }
    }

    private static void ApplyNow(int playerId, int[] chunks)
    {
        // ── 本次进房已被拒绝(会议进行中) → 一律不应用 ──
        //   不占用重连次数: 玩家并没有真的回来, 让他散会后重新进还能正常重连。
        if (ResumeTransition.RefusedByMeeting)
        {
            Log("恢复中止: 本次重连已取消(会议进行中), 不应用任何状态。");
            _hasPendingState = false;
            _pendingChunks = null;
            _positionApplyAt = 0f;
            return;
        }

        // ── 重连次数检查 ──
        //   每局最多允许 N 次(默认 3)。超出后拒绝恢复状态,
        //   玩家只能等这一局结束、在大厅重新加入。
        var limit = ResumeOptions.Limit;

        if (limit > 0 && _resumeCount >= limit)
        {
            ResumeRejected = true;
            Log("重连拒绝: 本局已重连 " + _resumeCount + " 次, 达到上限 " + limit
                + " → 不再恢复状态, 请等待本局结束。");

            try
            {
                ShowPopup("本局重连次数已用完(" + limit + " 次), 请等待本局结束后再加入");
            }
            catch { }

            return;
        }

        // ⚠️ 计数不在这里加。
        //   失败的重连(例如会议期间被房主踢出、或者恢复途中掉线)不该占用上限。
        //   所以先记下"本次待计数", 等状态真的应用成功之后再加(见本方法末尾)。

        ResumeRejected = false;

        _isResumedPlayer = true;
        _resumedAsGhost = false;   // 由下面的 snap.IsDead 决定
        var snap = NebulaStateCodec.Decode(chunks);

        // 恢复一律应用到【本机玩家】。
        //
        //   服务端保留的 playerId 只用于标识"这包是谁的状态"。新方案里身体是用
        //   空闲 id 新建的, 所以按旧 playerId 去 AllPlayers 里找人必然找不到
        //   (曾报过 "恢复中止: 找不到 playerId=1")。应用对象就是本机。
        Player? target = null;
        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp != null)
            {
                target = Player.GetPlayer(lp.PlayerId);
            }
        }
        catch { }

        if (target == null)
        {
            // 不丢包: 本机角色/NoS 玩家晚到时先补注册, 放回暂存, 下一轮再试(最多 15 秒)。
            //   早先这里直接 return, 恢复包就永久丢了 —— 表现是"重连后没职业"。
            var nowRetry = UnityEngine.Time.unscaledTime;
            if (_applyRetryUntil <= 0f)
            {
                _applyRetryUntil = nowRetry + 15f;
                Log("恢复推迟: 本机 NoS 玩家尚未注册 → 补注册后重试(最多 15 秒)。");
            }

            if (nowRetry < _applyRetryUntil)
            {
                try { ResumeTransition.EnsureLocalNoS(); } catch { }
                _hasPendingState = true;
                _pendingPlayerId = playerId;
                _pendingChunks = chunks;
                return;
            }

            _applyRetryUntil = 0f;
            Log("恢复中止: 15 秒内本机 NoS 玩家仍未注册(LocalPlayer 为空)。");
            return;
        }

        _applyRetryUntil = 0f;

        if (NebulaStateCodec.Apply(target, PlayerControl.LocalPlayer, snap, out var detail))
        {
            Log("恢复完成 服务端playerId=" + playerId + " :: " + detail);

            // 状态真的应用成功之后才计入"本局重连次数" ——
            // 失败的重连(会议被踢/中途掉线)不占上限。
            _resumeCount++;
            Log("重连计数: 第 " + _resumeCount + " 次(上限 " + limit + ")。");

            // 本次重连已成功 → 之后再掉线按普通掉线提示, 不再算"重连失败"。
            ReconnectPrompt.NoteAttemptSucceeded();
        }
        else
        {
            Log("恢复未生效 服务端playerId=" + playerId + " :: " + detail);
        }

        // ── 生死态: 按【掉线前】的原状恢复 ──
        //
        //   掉线前活着 → 保持活人, 回来可以继续正常游玩;
        //   掉线前已死 → 恢复为灵魂。
        //
        //   判定依据是状态包里的 IsDead(掉线那一刻采集的), 与"职业"无关。
        //   注意: 这里写两层 —— 香草层(别人/会议怎么看你) + 模组层(本机渲染/HUD)。
        try
        {
            var lp = PlayerControl.LocalPlayer;

            if (lp != null)
            {
                // 会议中重连、代坐期间被放逐: 状态包里还是"掉线前存活", 但这个人已经被投出去了。
                var exiledInMeeting = MeetingSeat.ConsumeExiled();

                if (snap.IsDead || exiledInMeeting)
                {
                    NebulaStateCodec.MakeGhostLocal(lp, target);
                    _resumedAsGhost = true;

                    // 上报死者身份: 服务端据此广播名册, 让全场都按"已死"处理。
                    DeadRoster.MarkSelfResumed(true);

                    Log(exiledInMeeting
                        ? "重连终态: 会议中代坐时被放逐 → 恢复为灵魂。"
                        : "重连终态: 掉线前已死亡 → 恢复为灵魂。");
                }
                else
                {
                    // 掉线前是活的 —— 按房主命令决定:
                    //   +on  → 保持活人
                    //   +off → 转为灵魂
                    if (ResumeOptions.ReviveEnabled)
                    {
                        NebulaStateCodec.MakeAliveLocal(lp, target);
                        _resumedAsGhost = false;
                        DeadRoster.MarkSelfResumed(false);
                        Log("重连终态: 掉线前存活 + 命令ON → 保持活人, 可继续游玩。");
                    }
                    else
                    {
                        NebulaStateCodec.MakeGhostLocal(lp, target, true);
                        _resumedAsGhost = true;
                        DeadRoster.MarkSelfResumed(true);
                        Log("重连终态: 掉线前存活 + 命令OFF → 按设置转为灵魂。");
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            Log("重连终态生死恢复异常: " + e.Message);
        }

        // ── 结算状态: 把「掉线」改成「已重连」 ──
        //   掉线时模组会把状态标成掉线, 重连后不改回来, 结算界面会一直显示掉线。
        try
        {
            ReconnectStateTag.MarkReconnected(target);
        }
        catch (System.Exception se)
        {
            Log("结算状态更新异常: " + se.Message);
        }

        // ── 任务恢复 ──
        //   优先用状态包里采集的任务(不依赖服务端补发资料);
        //   没有时才回退到香草的资料层。
        try
        {
            NebulaStateCodec.ApplyTasks(PlayerControl.LocalPlayer, snap);
        }
        catch (System.Exception te)
        {
            Log("任务恢复异常: " + te.Message);
        }

        // ── 位置恢复 ──
        //   用 RpcSnapTo 瞬移回掉线时的坐标; 延迟一帧以免和角色生成撞车。
        //   掉线后开过会 → 不回掉线处, 改为散会后的默认位置(会议桌), 见 RejoinNotice.cs。
        try
        {
            var placed = snap;
            var restoreOld = RejoinPlacement.Decide(ref placed);
            if (placed.HasPosition)
            {
                _pendingPosition = placed;
                _positionApplyAt = UnityEngine.Time.unscaledTime + 0.35f;
                Log(restoreOld ? "位置恢复: 已排程(0.35 秒后瞬移回掉线处)。" : "位置: 已排程(0.35 秒后瞬移到会议桌)。");
            }
            else
            {
                _positionApplyAt = 0f;
            }
        }
        catch (System.Exception pe)
        {
            Log("位置决定异常: " + pe.Message);
        }

        // 房主序列化可能把本机重新拉回活人态 → 仅对"灵魂"持续对账。
        if (_resumedAsGhost)
        {
            _ghostAssertUntil = UnityEngine.Time.unscaledTime + GhostAssertSeconds;
            _nextGhostAssert = 0f;
            Log("死亡态断言已开始 " + (int)GhostAssertSeconds + " 秒。");
        }
        else
        {
            _ghostAssertUntil = 0f;
            Log("死亡态断言: 本机为活人, 不需要断言。");
        }
    }

    // ── 本机「踢人分」清零 ──
    //
    // 背景(反编译 AmongUs.Data.Player.PlayerBanData 得到):
    //   游戏本体有一个【纯本地】的惩罚机制 —— 在对局中途非正常退出会累加 banPoints,
    //   攒够 POINTS_UNTIL_BAN_STARTS 之后, 就要等 banPoints * MINUTES_PER_BAN_POINT 分钟
    //   才能再进对局, 弹窗文案就是「故意断线后需等待 N 分钟才可加入下一场游戏」。
    //   分数存在本机存档里:
    //       %USERPROFILE%\AppData\LocalLow\Innersloth\Among Us\player.amogus  ("ban" 字段)
    //
    //   这个惩罚和 Fmpostor 无关 —— 服务端源码里没有任何地方发送 IntentionalLeaving,
    //   也没有任何「罚等 N 分钟」的实现。是客户端自己记的分、自己算的时长、自己弹的窗。
    //
    //   而我们在掉线后会调用 CoFindGameInfoFromCodeAndJoin 立即发起一次重新加入,
    //   这条路径更容易被算作「对局中途异常离开」, 于是分数不断累积 ——
    //   表现就是惩罚时长从 30 分钟涨到 35 分钟。
    //
    //   ★ 观测数据: 第一次清零时分数是 9, 与"惩罚时长随分数累积增长"一致
    //     (30 分钟 → 35 分钟 就是分数在往上走)。
    //
    //   ⚠️ 必须【反复清】, 不能只清一次。
    //     第一版写成"清一次就完事", 结果: 启动时清了 9, 但这一局里新掉一次线又累加了分,
    //     于是关掉游戏再进时惩罚依旧。正确做法是每次回到主菜单/大厅就把分抹平。
    //
    //   同时重置 BanPoints 与 PreviousGameStartDate 两个:
    //     · BanPoints            = 累积的踢人分(惩罚时长的来源)
    //     · PreviousGameStartDate = 上一局开始时间(残余的惩罚窗口靠它判断"还剩多久")
    //   只清前者的话, 残余窗口可能仍然生效。
    private static float _nextBanResetAt;

    /// <summary>上次记过日志的分数值。避免同一个值反复打日志刷屏。</summary>
    private static float _lastBanPointsLogged = -1f;

    // ══════════════════════════════════════════════════════════
    // 环境兜底: 已经在对局里, 但界面还停在大厅态
    // ══════════════════════════════════════════════════════════
    //
    // ▸ 为什么需要它
    //   正常流程是"服务端发续局信号(kind=3) → 客户端做过渡"。但已观察到这种情况:
    //   中途加入(或会议被踢后再进)的客户端【一条 kind=3 都没收到】,
    //   于是过渡从来不做 —— 表现就是:
    //       没有职业(连 Unassigned 都没有)、大厅 UI 还叠在画面上。
    //   这不是"过渡失败", 而是"根本没人触发过渡"。
    //
    // ▸ 判断条件(必须全部满足才兜底, 避免干扰正常开局)
    //   · 还没做过过渡, 也没被会议拒绝;
    //   · 已经在对局场景里(船在、本机角色在、NoS 里已经有玩家);
    //   · 本机是"对局开始之后才出现的"(SpawnGuard 的基准里没有自己),
    //     或者本机被判定成观战者;
    //   · 界面还停在大厅态(大厅对象还在 / 没切到 Started)。
    //
    //   正常开局的玩家命中不了最后两条, 所以不会被误触发。

    private static float _fallbackAt;

    private static void TickEnvironmentFallback()
    {
        if (ResumeTransition.Done || ResumeTransition.RefusedByMeeting)
        {
            return;
        }

        var client = AmongUsClient.Instance;
        if (client == null || client.GameState == InnerNetClient.GameStates.NotJoined)
        {
            return;
        }

        if (ShipStatus.Instance == null || PlayerControl.LocalPlayer == null)
        {
            return;
        }

        // 会议期间只有观战者可以照常入场(重连者由拒绝逻辑处理)。
        if (MeetingFlag.Active && !SpectatorSeat.AmSpectator)
        {
            return;
        }

        // 房主支持会议中进入时, 过渡本来就要等散会才做(会议画面由 MeetingDefer 负责):
        //   会议期间不必每 2 秒再请求一次(原来会刷几十条"兜底"日志)。散会后这里照常兜底。
        if (MeetingFlag.Active && MeetingFlag.HostSupportsMeetingJoin)
        {
            return;
        }

        if (!SpectatorSeat.AmSpectator && !SpawnGuard.IsMidRoundLocal())
        {
            return;   // 正常开局玩家, 不插手
        }

        var lobbyish = LobbyBehaviour.Instance != null
                       || GameStartManager.Instance != null
                       || client.GameState != InnerNetClient.GameStates.Started;

        if (!lobbyish)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _fallbackAt)
        {
            return;
        }

        _fallbackAt = now + 2f;

        Log("兜底: 已在对局中但界面仍是大厅态(没有收到续局信号) → 主动执行一次过渡。");

        if (_unrecognizedCheckAt <= 0f)
        {
            _unrecognizedCheckAt = now + UnrecognizedWaitSeconds;
        }

        try
        {
            ResumeTransition.Run(SpectatorSeat.AmSpectator);
        }
        catch (System.Exception e)
        {
            Log("兜底过渡异常: " + e.GetType().Name + " : " + e.Message);
        }
    }

    // ── 会议中进房、还没有身体 → 自行离开 ──
    //   这种连接在会议结束前拿不到身体(房主会议期间不生成), 最终会被房主踢出;
    //   而"被踢出"在服务端不会归还保槽, 散会后再进就恢复不了职业。
    //   自己先离开属于普通离开, 服务端会把保槽原样还回来。
    //   判据很窄: 已进对局场景、不是房主、本机还没有身体、过渡尚未完成、房主说会议进行中。
    //   正常开局与已经回到对局的人都不会命中。
    private static void TickBodylessMeetingRefusal()
    {
        if (ResumeTransition.Done || ResumeTransition.RefusedByMeeting)
        {
            return;
        }

        var client = AmongUsClient.Instance;
        if (client == null || client.AmHost)
        {
            return;
        }

        if (client.GameState != InnerNetClient.GameStates.Joined
            && client.GameState != InnerNetClient.GameStates.Started)
        {
            return;
        }

        // 有身体 = 正常路径; 大厅里 = 根本不是对局中途。
        if (PlayerControl.LocalPlayer != null || LobbyBehaviour.Instance != null)
        {
            return;
        }

        if (!MeetingFlag.Active)
        {
            return;
        }

        // 还没收到房主的会议广播(里面带"是否支持会议中进入"): 先等, 不能按"不支持"处理。
        //   实测: 刚进房时本机已经有会议界面(Active=true), 但房主的广播晚到几百毫秒 ——
        //   原来在这段空档里就把自己踢出了房间(新玩家第一次进房弹出大厅界面后被移出)。
        //   房主每 3 秒广播一次; 不支持会议中进入的房主照旧会在会议后处理这个连接。
        if (!MeetingFlag.HasFreshInfo)
        {
            return;
        }

        // 房主支持会议中进入: 留在房间里等身体和散会, 服务端会在散会后续局。
        if (MeetingFlag.HostSupportsMeetingJoin)
        {
            return;
        }

        Log("会议进行中进入房间且尚无角色 → 自行离开, 让服务端保留位置(不计入重连次数)。");
        ResumeTransition.RefuseByMeeting("会议中进入(尚无身体)");
    }

    // ── 中途入场却没被服务端认出来 ──
    //   兜底过渡跑了(说明没有 kind=3), 之后一段时间内也既没有状态包(kind=2)
    //   也没有观战判定(kind=7) → 服务端没有把这次加入当成重连或观战。
    //   典型原因: 会议期间那次被拒的重连已经把保槽消耗掉了(服务端侧问题)。
    //   客户端无法凭空恢复职业, 这里只负责把情况说清楚, 不自行判定身份。
    private static float _unrecognizedCheckAt;
    private const float UnrecognizedWaitSeconds = 15f;

    private static void TickUnrecognizedJoin()
    {
        if (_unrecognizedCheckAt <= 0f)
        {
            return;
        }

        if (ShipStatus.Instance == null)
        {
            _unrecognizedCheckAt = 0f;
            return;
        }

        if (_isResumedPlayer || SpectatorSeat.AmSpectator)
        {
            _unrecognizedCheckAt = 0f;
            return;
        }

        if (UnityEngine.Time.unscaledTime < _unrecognizedCheckAt)
        {
            return;
        }

        _unrecognizedCheckAt = 0f;
        Log("中途入场未被识别: " + (int)UnrecognizedWaitSeconds
            + " 秒内没有收到状态包(kind=2)或观战判定(kind=7) → 服务端没有找到本机的保留位置, 职业无法恢复。");
        ShowPopup(Translate("prompt.slotLost",
            "服务器没有找到你的保留位置（可能是会议期间的重连已用掉了它），本局无法恢复原职业。"));
    }

    private static string Translate(string key, string fallback)
    {
        try
        {
            var v = NebulaAPI.Language.Translate(key);
            if (!string.IsNullOrEmpty(v) && !v.StartsWith("prompt.") && !v.StartsWith("*"))
            {
                return v;
            }
        }
        catch { }

        return fallback;
    }

    private static void TickBanPointReset()
    {
        // 只在大厅/主菜单阶段做(GameState == NotJoined)。
        // 对局中改存档会和游戏的保存逻辑打架, 而且进对局后分数已经不影响这一局了。
        if (!NebulaResumeEntry.IsInMainMenu())
        {
            return;
        }

        // 每秒最多查一次, 避免每帧写存档
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextBanResetAt)
        {
            return;
        }

        _nextBanResetAt = now + 1f;

        try
        {
            if (!AmongUs.Data.DataManager.IsPlayerLoaded)
            {
                return;   // 存档还没加载完, 下次再试
            }

            var ban = AmongUs.Data.DataManager.Player?.ban;
            if (ban == null)
            {
                return;
            }

            var pointsBefore = ban.BanPoints;

            if (pointsBefore != 0f)
            {
                ban.BanPoints = 0f;   // setter 触发 OnBanPointsChanged → 存档标脏 → 落盘

                // ⚠️ 只在【值真的变化】时打日志。
                //   注意: 游戏会把这个值重新读回来, 于是每次都读到同一个 9,
                //   日志被十几行重复的"9 → 0"刷满, 真正有用的信息全被埋掉。
                if (pointsBefore != _lastBanPointsLogged)
                {
                    _lastBanPointsLogged = pointsBefore;
                    Log("本机踢人分已清零: " + pointsBefore + " → 0 (游戏本体的本地断线惩罚)");
                }
            }

            // 残余惩罚窗口: 把"上一局开始时间"一起清掉, 否则 BanMinutesLeft 可能仍然 > 0
            try
            {
                var dateBefore = ban.PreviousGameStartDate;
                if (dateBefore.Year > 1)
                {
                    ban.PreviousGameStartDate = Il2CppSystem.DateTime.MinValue;
                    Log("本机残留惩罚窗口已清除(上一局开始时间 " + dateBefore.Year + "-"
                        + dateBefore.Month + "-" + dateBefore.Day + ")");
                }
            }
            catch (System.Exception de)
            {
                Log("清残留惩罚窗口失败(不影响分数清零): " + de.Message);
            }

        }
        catch (System.Exception e)
        {
            Log("清零踢人分失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    // ── 灵魂态对账 ──
    // 房主序列化可能把本机拉回活人态, 因此在一段时间内周期性重设。
    private static float _ghostAssertUntil;
    private static float _nextGhostAssert;
    /// <summary>
    ///     灵魂态对账窗口(秒)。
    ///     ⚠️ 从 12 秒延长到 45 秒: v5 起玩家是【自己回主菜单、自己重新进房间】,
    ///       中间可能停留十几秒再进来, 而且重连走的是全新建连流程,
    ///       房主序列化把本机拉回活人态的时刻会晚得多。窗口太短会来不及补。
    /// </summary>
    private const float GhostAssertSeconds = 45f;
    private const float GhostAssertIntervalSeconds = 0.25f;

    /// <summary>由 Tick 调用: 在窗口期内周期性重设灵魂态。</summary>
    private static void TickGhostStateAssert()
    {
        if (UnityEngine.Time.unscaledTime >= _ghostAssertUntil)
        {
            return;
        }

        if (UnityEngine.Time.unscaledTime < _nextGhostAssert)
        {
            return;
        }

        _nextGhostAssert = UnityEngine.Time.unscaledTime + GhostAssertIntervalSeconds;

        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.Data == null)
            {
                return;
            }

            if (lp.Data.IsDead)
            {
                return;   // 已经是灵魂态, 房主没有覆盖它
            }

            // 被拉回活人态 → 重新灵魂化。
            NebulaStateCodec.MakeGhostLocal(lp);
            Log("房主序列化把本机拉回活人态, 已重新恢复灵魂态。");
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════
    // 采集循环
    // ══════════════════════════════════════════════════════════

    private static float _nextTick;

    // ── 可见性统计 ──
    // 上报成功是静默的(只在失败时打日志), 于是"到底有没有发出去"在日志里看不见。
    // 这几个计数让心跳行能直接说明传输是否在工作。
    private static int _sentTotal;
    private static int _lastSentSnapshot;
    private static int _maxChunkLen;
    private static int _minChunkLen = int.MaxValue;
    private static readonly System.Collections.Generic.Dictionary<byte, int> _lastFingerprint =
        new System.Collections.Generic.Dictionary<byte, int>();

    private static int _tickCount;

    /// <summary>由 ResumeDriver 的 Update() 每帧调用。</summary>
    internal static void Tick()
    {
        try
        {
            _tickCount++;

            // 心跳: 每 300 帧打一条。用来区分"驱动没挂上"和"挂上了但没玩家/不在对局"。
            if (_tickCount % 300 == 1)
            {
                var hasGame = NebulaAPI.CurrentGame != null;
                var players = hasGame ? Player.AllPlayers.Count() : 0;

                var sinceLast = _sentTotal - _lastSentSnapshot;
                _lastSentSnapshot = _sentTotal;

                var chunkInfo = _maxChunkLen == 0
                    ? "状态包长度=尚未上报"
                    : ("状态包长度=" + _minChunkLen + "~" + _maxChunkLen);

                Log("心跳 #" + _tickCount
                    + " :: 对局=" + (hasGame ? "有" : "无")
                    + " 玩家数=" + players
                    + " 累计上报=" + _sentTotal
                    + " 本轮新增=" + sinceLast
                    + " " + chunkInfo);
            }

            // 以下几项需要比 1 秒更快的反应, 放在节流之前(各自内部再节流)。
            try { StalePlayerSweep.Tick(); } catch { }
            try { SpawnGuard.FastFillRoles(); } catch { }
            try { CosmeticsWarmup.Tick(); } catch { }
            try { EmergencySync.Tick(); } catch { }
            try { MeetingSeat.Tick(); } catch { }
            try { MeetingDefer.Tick(); } catch { }
            try { MeetingProxy.Tick(); } catch { }
            try { RejoinPlacement.Tick(); } catch { }
            try { RejoinBanner.Tick(); } catch { }

            var now = UnityEngine.Time.unscaledTime;
            if (now < _nextTick)
            {
                return;
            }

            _nextTick = now + (HeartbeatMs / 1000f);

            // 掉线询问的状态机推进(冷启动询问 / 重连成功后复位)
            // 房主端补生成身体: 给"对局中却没有身体"的连接(重连回来的人)造身体。
            // 服务端做不到这件事 —— 它的 InnerPlayerControl.SerializeAsync 是故意未实现的,
            // 而原版里角色的生成消息只由房主客户端发出。
            try { HostBodySpawner.Tick(); } catch { }

            // 掉线提示 + 记录本局房间号。
            // 注: 不再有自动重连 —— 掉线后只给原生弹窗追加一行提示(带房间号),
            //     由玩家自己回主菜单、重新进服务器、用「输入房间号」回去。
            //     重连成功与否由服务端的续局信号(kind=3)+ 状态包(kind=2)驱动。
            try { ReconnectPrompt.Tick(); } catch { }
            // 会议状态广播(房主发, 其余客户端收): 必须在续局判定之前跑。
            try { MeetingFlag.Tick(); } catch { }
            try { TickBodylessMeetingRefusal(); } catch { }
            try { ResumeTransition.TickHideLobbyUi(); } catch { }
            try { ResumeTransition.TickDataDiagnostics(); } catch { }
            try { ResumeTransition.TickDeferredRun(); } catch { }
            try { ResumeTransition.TickLeaveRoomReset(); } catch { }
            try { ResumeTransition.TickRefusalKick(); } catch { }
            try { TickPendingState(); } catch { }
            try { SyncAssist.Tick(); } catch { }
            try { RoleSync.Tick(); } catch { }

            // 离开对局 → 重连计数清零
            if (ShipStatus.Instance == null && _resumeCount > 0)
            {
                _resumeCount = 0;
                ResumeRejected = false;
            }
            try { SpawnGuard.Tick(); } catch { }
            try { TickSelfGhostAssert(); } catch { }
            try { DeadRoster.Tick(); } catch { }
            try { SpectatorSeat.Tick(); } catch { }
            try { SpectatorSeat.TickHostRoleBroadcast(); } catch { }
            try { TickPendingPosition(); } catch { }
            try { NebulaStateCodec.TickTaskVerify(); } catch { }
            try { OutfitSync.Tick(); } catch { }
            try { RoomCapability.Tick(); } catch { }
            try { MeetingVoteGuard.Tick(); } catch { }

            // 重连后的灵魂态对账(房主序列化可能把本机拉回活人态)
            try { TickGhostStateAssert(); } catch { }

            // 清零本机「踢人分」(游戏本体的本地惩罚机制)
            try { TickBanPointReset(); } catch { }

            // 进房判定诊断: 服务端到底认没认这次加入是"重连"
            try { TickEnvironmentFallback(); } catch { }
            try { TickUnrecognizedJoin(); } catch { }

            CaptureAll();
        }
        catch (System.Exception e)
        {
            Log("Tick 异常: " + e.Message);
        }
    }

    private static void CaptureAll()
    {
        if (NebulaAPI.CurrentGame == null)
        {
            if (_lastFingerprint.Count != 0)
            {
                Log("采集暂停: 当前不在对局中。");
            }

            _lastFingerprint.Clear();
            return;
        }

                foreach (var player in Player.AllPlayers)
        {
            if (player == null)
            {
                continue;
            }

            try
            {
                var playerId = ((IArchivedPlayer)player).PlayerId;
                var snap = NebulaStateCodec.Capture(player);

                // ⚠️ 【必须无条件按心跳上报, 不能"变了才报"】—— 这是服务端的契约要求。
                //
                //   服务端（ReconnectConfig.ModStateCollectMs 的注释）明确写着:
                //     "the 'ask the remaining clients for a snapshot at disconnect time'
                //      round trip was replaced by the always-on live cache
                //      (clients report on a heartbeat, so the cache is already fresh
                //       when a disconnect happens)."
                //
                //   也就是说: 服务端持有的是【常驻缓存】, 它假定客户端在【心跳式】持续刷新。
                //   如果这里按指纹去重（状态没变就跳过），那么 12 个普通船员的指纹全都
                //   恒定不变 —— 第一轮报完之后就再也不报了 —— 服务端的缓存会一直停在
                //   那个旧值上；一旦此时有人掉线，快照拿到的就是过期数据。
                //
                //   代价: 12 人 × 每秒 1 条 × 约 20 字节 ≈ 每秒 240 字节，可以忽略。
                ReportNebulaState(playerId, NebulaStateCodec.Encode(snap));
                _lastFingerprint[playerId] = Fingerprint(snap);
            }
            catch (System.Exception e)
            {
                Log("采集失败: " + e.Message);
            }
        }
    }

    /// <summary>
    ///     把快照压成一个整数指纹, 用于"变了才报"。
    ///
    ///     ⚠️ 职业参数必须参与指纹: 招募类职业(如豺狼)在招募侧kick 时【职业Id 不变,
    ///       只有 RoleArguments 变】(招募次数 +1)。若指纹只看 Id, 这次变更就被判成
    ///       "没变化"而不上报, 恢复出来会是旧的招募次数。
    /// </summary>
    private static int Fingerprint(NebulaStateCodec.Snapshot snap)
    {
        unchecked
        {
            var h = 17;

            h = Mix(h, snap.Role);
            h = Mix(h, snap.GhostRole);
            h = (h * 31) + (snap.IsDead ? 1 : 0);

            var mods = snap.Modifiers ?? System.Array.Empty<NebulaStateCodec.AssignableState>();
            for (var i = 0; i < mods.Length; i++)
            {
                h = Mix(h, mods[i]);
            }

            return h;
        }
    }

    private static int Mix(int h, NebulaStateCodec.AssignableState st)
    {
        unchecked
        {
            h = (h * 31) + st.Id;

            var args = st.Args ?? System.Array.Empty<int>();
            h = (h * 31) + args.Length;
            for (var i = 0; i < args.Length; i++)
            {
                h = (h * 31) + args[i];
            }

            return h;
        }
    }

}
