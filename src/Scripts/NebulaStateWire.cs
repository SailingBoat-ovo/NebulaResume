// ============================================================
// NebulaStateWire.cs — 与服务端之间的传输层
//
// ▸ 通道
//   走 NOS 自己的 RPC 通道(香草 RpcCalls = 128)。
//   这里构造一个【运行时】的 RemoteProcess<int[]>, 不需要 [NebulaRPC] 特性:
//   它的构造函数会把 "名字的常量哈希" 注册进 AllNebulaProcess,
//   服务端只要用同一个哈希发包, 就能派发到这里。
//
// ▸ ★ 序列化类型是 Virial 的, 不是 Hazel 的
//   RemoteProcess<T> 的收发委托签名是:
//       Action<Virial.Utilities.MessageWriter, T>
//       Func<Virial.Utilities.MessageReader, T>
//   (用 Hazel.MessageWriter/MessageReader 会直接 CS1503 编译失败)
//   这两个类型的编码是【小端】, 与 Hazel / Fmpostor 的 reader.ReadInt32() 完全一致,
//   所以服务端可以按同样格式解析 —— 见下方 WriteIntArray。
//
// ▸ 帧格式(int[])
//   [0] Kind      KindReport = 1 / KindRestore = 2 / KindResume = 3 / KindSpawn = 4
//   [1] playerId  游戏内玩家槽位号(GamePlayerId)
//   [2..]         NebulaStateCodec 的输出(服务端不解析其内容)
//
//   ⚠️ 上报帧【不带 clientId】—— 服务端从连接本身取得调用者身份, 不信客户端自报。
//      这是防伪造的关键: 伪造一个 clientId 没有意义。
//
//   ⚠️ 服务端识别"这是不是我们的包"靠【长度精确校验】, 不靠哈希 ——
//      哈希是 NOS 内部扩展方法算的, 服务端无法复刻, 猜错就永远收不到。
//      线上格式恰好是: [hash:int32][count:int32][count * int32]
// ============================================================

using Nebula.Modules;

namespace NebulaResume;

internal static class NebulaStateWire
{
    internal const int KindReport = 1;
    internal const int KindRestore = 2;

    /// <summary>
    ///     续局信号(服务端 → 客户端)。
    ///
    ///   ▸ 它解决什么
    ///     客户端无法凭自己区分"我这次进房间是【普通加入】还是【掉线后回来】"。
    ///     没有这个信号时, 只能靠猜(IsInGame / ShipStatus), 容易误判:
    ///     普通加入后也去做续局过渡, 或者重连回来却不做过渡(没 HUD、动不了)。
    ///
    ///   ▸ 为什么编号是 3
    ///     我们的传输通道是 NOS 的 RpcCalls = 128, 官方 RpcCalls 枚举最大只到 66,
    ///     128 本身就在自定义区。在这个通道内我们用 kind 编号区分消息:
    ///         1 = 上报(客户端 → 服务端)
    ///         2 = 恢复(服务端 → 客户端, 带状态包)
    ///         3 = 续局(服务端 → 客户端, 只带语义, 无数据)
    ///     全部为本方案自定义的编号与命名。
    ///
    ///   ▸ 收到之后做什么
    ///     执行续局过渡(见 ResumeTransition): 就地切到"正在对局中"的 UI 状态,
    ///     并把全场玩家注册进本机 NoS。然后等服务端下发 kind=2 状态包还原模组状态。
    /// </summary>
    internal const int KindResume = 3;

    /// <summary>
    ///     补生成指令(服务端 → 房主)。
    ///
    ///   ▸ 为什么需要它
    ///     重连者的角色要由房主生成, 而香草的 CreatePlayer 会从 GameData 里取一个
    ///     空闲 PlayerId —— 那会【新开】一个玩家, 不是"恢复原身份"。服务端手里
    ///     才知道该恢复的那个 PlayerId(保槽里存的)。所以由服务端把
    ///     (clientId, 原 PlayerId) 告知房主, 房主按香草同样的四个动作精确还原。
    ///
    ///   ▸ 帧格式: [KindSpawn, clientId, 原 playerId]
    /// </summary>
    internal const int KindSpawn = 4;

    /// <summary>
    ///     死亡上报(重连者 → 服务端)。格式: [5, playerId]
    ///     重连者周期性发送, 服务端据此把它的 clientId 记进死亡名册。
    ///     ⚠️ 不带 clientId: 服务端从连接本身取调用者身份, 防止伪造他人。
    /// </summary>
    internal const int KindGhostReport = 5;

    /// <summary>
    ///     死亡名册(服务端 → 所有人)。格式: [6, 人数 N, clientId × N]
    ///     全量下发而不是"某某变鬼了"的事件: 幂等、丢包自愈、后加入者也能一次拿到完整状态。
    /// </summary>
    internal const int KindGhostRoster = 6;

    /// <summary>
    ///     观战判定(服务端 → 客户端)。格式: [7, 1/0]
    ///     1 = "你这次进来是观战者"(全新玩家中途加入已开始的对局);
    ///     0 = 不是观战者。
    ///     观战者与重连者的处理不同: 重连者恢复原职业, 观战者一律中立观战职业。
    /// </summary>
    internal const int KindSpectator = 7;

    /// <summary>
    ///     观战名单(服务端 → 所有人)。格式: [8, 人数 N, clientId × N]
    ///     与死亡名册同样是全量下发: 幂等、可自愈、后加入者一次拿全。
    /// </summary>
    internal const int KindSpectatorRoster = 8;

    /// <summary>
    ///     房间能力声明(房主 → 服务端)。格式: [9, 协议版本, 能力标志, 观战上限]
    ///
    ///     服务端只有在收到这条声明、且能力标志允许时，才会在【对局已开始】的
    ///     情况下放行新玩家作为观战者。没声明过的房间保持原有行为 ——
    ///     新玩家一律被拒，不会误放行。
    /// </summary>
    internal const int KindCapability = 9;

    /// <summary>
    ///     会议状态(房主 → 全场)。格式: [10, 1/0]
    ///
    ///   ▸ 为什么需要它
    ///     会议界面是【会议开始那一刻】由各客户端自己按 RpcStartMeeting 建的。
    ///     中途重连回来的人没有收到那条 RPC, 本机根本没有 MeetingHud,
    ///     所以"会议中禁止重连"不可能靠本机状态判断出来。
    ///     房主知道, 由它周期性广播, 其余客户端据此判断。
    ///
    ///   ▸ 1 = 会议(或放逐动画)进行中, 0 = 不在会议中
    /// </summary>
    internal const int KindMeeting = 10;

    /// <summary>
    ///     进程名 —— 服务端必须用同一个字符串算哈希。
    ///     ⚠️ 改名就等于换协议, 两端要一起改。
    /// </summary>
    private const string ProcessName = "NebulaResume.State.v1";

    private static RemoteProcess<int[]> _process;

    /// <summary>
    ///     本次进房是否收到过"续局信号(kind=3)"或"状态包(kind=2)"。
    ///     收到过就说明这是一次【重连尝试】—— 之后如果被移出房间,
    ///     要按"重连失败"提示, 而不是按普通掉线/主动退出处理。
    /// </summary>
    internal static bool ResumeAttemptSeen { get; private set; }

    internal static void ResetDiagnostics()
    {
        ResumeAttemptSeen = false;
    }

    /// <summary>
    ///     注册收发端点。幂等。
    /// </summary>
    internal static void EnsureReady()
    {
        if (_process != null)
        {
            return;
        }

        _process = new RemoteProcess<int[]>(
            ProcessName,
            WriteIntArray,
            ReadIntArray,
            OnReceive);
    }

    /// <summary>
    ///     把一帧发给服务端(实际是广播给同房间的客户端, 由服务端截获处理)。
    /// </summary>
    /// <remarks>
    ///     ⚠️ 必须先在【本机还没进对局 / 本地角色还没生成】时直接返回。
    ///
    ///     注意: 在这种窗口里调用 Invoke 会抛
    ///         Il2CppException : System.NullReferenceException
    ///     而且是【每帧一次】—— 日志被"发送失败"刷满几千行, 真正有用的信息全被埋掉。
    ///     根因是底层发包路径要解引用本地玩家 / 连接, 那时它们还是 null。
    ///
    ///     另外把失败日志做成【去重】: 同一种失败只报一次, 不再刷屏。
    /// </remarks>
    internal static void Send(int[] frame)
    {
        // 不在对局里 → 没有任何东西要上报
        if (!NebulaResumeEntry.IsInGame())
        {
            return;
        }

        // 本地角色还没生成 → 发包路径会空引用
        if (PlayerControl.LocalPlayer == null)
        {
            return;
        }

        try
        {
            EnsureReady();
            _process.Invoke(frame);
        }
        catch (System.Exception e)
        {
            var key = e.GetType().Name + ":" + e.Message;
            if (key != _lastSendError)
            {
                _lastSendError = key;
                NebulaResumeEntry.Log("发送失败(同类失败只报一次): " + key);
            }
        }
    }

    /// <summary>上次报过的发送失败, 用于去重。</summary>
    /// <summary>
    ///     不检查对局状态的原始发送。
    ///
    ///   为什么需要: <see cref="Send"/> 要求 GameState == Started, 而
    ///   房主在大厅时是 Joined —— 大厅阶段的上报会被静默丢弃。
    ///   房间能力声明必须在大厅就能发出去(服务端要在有人尝试加入之前知道),
    ///   所以这里只检查"连接在房间里 + 本机角色存在", 不看对局是否开始。
    ///
    ///   返回是否真的调用了发送接口 —— 调用方据此决定要不要记录"已声明"。
    /// </summary>
    internal static bool SendRaw(int[] frame)
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null)
            {
                return false;
            }

            // 必须在某个房间里(大厅或对局), 但不要求已开局。
            if (client.GameState == InnerNetClient.GameStates.NotJoined)
            {
                return false;
            }

            // 发送路径要解引用本机角色, 没有就会抛。
            if (PlayerControl.LocalPlayer == null)
            {
                return false;
            }

            EnsureReady();
            _process.Invoke(frame);
            return true;
        }
        catch (System.Exception e)
        {
            var key = e.GetType().Name + ":" + e.Message;
            if (key != _lastSendError)
            {
                _lastSendError = key;
                NebulaResumeEntry.Log("发送失败(同类失败只报一次): " + key);
            }

            return false;
        }
    }

    private static string _lastSendError;

    // ── 收到服务端下发的帧 ──
    // 目前只有一种: 恢复包。
    // isCalledByMe = true 表示是本机自己 Invoke 触发的本地执行(此时不该当收到处理)。

    private static void OnReceive(int[] frame, bool isCalledByMe)
    {
        try
        {
            if (isCalledByMe || frame == null || frame.Length < 2)
            {
                // 本机发出的帧会先在本地执行一次, 这里直接忽略 ——
                // 上报帧的"本地执行"没有任何意义, 不该重复触发。
                return;
            }

            var kind = frame[0];
            var playerId = frame[1];

            // ★ kind=1 是【客户端 → 服务端】的上报帧。
            //   但 NOS 的 RemoteProcess.Invoke() 本质是【广播】, 服务端只是截获;
            //   所以同房间的其它客户端也会收到这一帧。
            //   每个窗口会收到上千条 kind=1, 造成带宽浪费与状态污染。
            //   这里直接丢弃 —— 上报帧只对服务端有意义。
            if (kind == KindReport)
            {
                return;
            }

            // ★ 收包探针: 先把"到底有没有包进来"这件事确定下来。
            //
            //   服务端日志明确写着 `sent resume signal` / `sent mod-state restore`,
            //   但客户端一行接收日志都没有 —— 无法区分这两种情况:
            //     (a) 包根本没送到(服务端目标对象/地址不对)
            //     (b) 包送到了, 但 kind 分支没走到
            //   所以这里对【每一个】进来的帧都打一条, 一次测试即可分清。
            NebulaResumeEntry.Log("收到帧: kind=" + kind + " playerId=" + playerId
                + " 长度=" + frame.Length);

            // ── kind=3 续局信号 ──
            //   服务端确认"你这次进房间是掉线后回来"。
            //   这时才做续局过渡(把本机 UI 切到"正在对局中"并重建本机 NoS)。
            if (kind == KindResume)
            {
                ResumeAttemptSeen = true;
                ReconnectPrompt.NoteAttempt("续局信号 kind=3");

                // 第 3 个值(新服务端才有): 掉线之后是否开过会 → 决定散会后的落位。
                RejoinPlacement.MeetingSinceDisconnect = frame.Length >= 3 && frame[2] != 0;
                NebulaResumeEntry.Log("续局信号: 掉线后" + (RejoinPlacement.MeetingSinceDisconnect ? "开过会 → 不回掉线处, 用散会默认位置。" : "没开过会 → 恢复掉线位置。"));
                FrameGuard.Arm();   // 本局进入重连保护期(帧级异常保护生效)

                // ★ 收到新的续局信号 = 一次新的重连尝试 → 复位上一次的过渡状态。
                //   不这么做的话, "被踢回主菜单后再重连"会命中 _done 而被静默跳过。
                ResumeTransition.BeginAttempt();
                // 服务端约定在【本机角色就位后】才发这个信号(载体=自己的角色 netId)。
                //
                //   角色已在场 → 走的是"重新加入 + 房主补生成"路径, 香草流程已把对局环境
                //   搭好(GameState 已是 Started、没有大厅可藏、NoS 的生成钩子已注册本机)。
                //   此时硬跑完整过渡反而有风险: OnGameStart 被二次调用、NoS 重复初始化。
                //   所以只补一个"确保 HUD 开启"的轻量动作。
                //
                //   角色不在场 → 保留完整过渡作为兜底(理论上服务端已推迟到角色就位,
                //   这个分支只防御时序异常)。
                var bodyReady = PlayerControl.LocalPlayer != null && ShipStatus.Instance != null;

                NebulaResumeEntry.Log("收到续局信号 (playerId=" + playerId + ") 角色就位=" + bodyReady + "。");

                try
                {
                    // ★ 无论身体是否就位, 都必须走完整过渡。
                    //
                    //   已知问题: 身体已经就位时若走"轻量路径", 结果
                    //     · 心跳显示 玩家数=0      → 本机 NoS 层是空的
                    //     · NOS 的通风管补丁每帧空引用 → 因为"重连者在 NoS 层是空的,
                    //       凡是碰本地 NoS 玩家的系统都会每帧空引用"
                    //     · 大厅 UI 没消失        → 隐藏大厅/大厅UI 这步被跳过了
                    //     · 职业没恢复            → NoS 层为空, SetRole 无从作用
                    //   所以完整过渡(隐藏大厅/开HUD/重建本机NoS)是不可省略的,
                    //   与身体是否存在无关。
                    ResumeTransition.Run();
                }
                catch (System.Exception re)
                {
                    NebulaResumeEntry.Log("续局过渡异常: " + re.GetType().Name + " : " + re.Message);
                }

                return;
            }

            // ── kind=4 补生成指令(服务端 → 房主) ──
            //   格式: [KindSpawn, clientId, 原 playerId]
            if (kind == KindSpawn)
            {
                if (frame.Length < 3)
                {
                    NebulaResumeEntry.Log("补生成指令长度不足(" + frame.Length + "), 丢弃。");
                    return;
                }

                var targetClientId = frame[1];
                var preservedPlayerId = frame[2];
                NebulaResumeEntry.Log("收到补生成指令: clientId=" + targetClientId
                    + " 原 playerId=" + preservedPlayerId + " → 交给房主端执行。");
                HostBodySpawner.OnServerDirective(targetClientId, preservedPlayerId);
                return;
            }

            // ── kind=7 观战判定(服务端 → 客户端) ──
            if (kind == KindSpectator)
            {
                var isSpectator = frame.Length >= 2 && frame[1] != 0;
                NebulaResumeEntry.Log("收到观战判定: " + (isSpectator ? "是观战者" : "不是观战者") + "。");

                if (isSpectator)
                {
                    // 服务端应当保证"重连者"与"观战者"互斥。这里加一道防御:
                    // 如果本机已经按重连流程恢复过(收到过 kind=2/3), 就不要再把它
                    // 当成观战者 —— 否则会把恢复好的原职业覆盖成观战职业。
                    if (NebulaResumeEntry.HasResumedAsReconnecter)
                    {
                        NebulaResumeEntry.Log("观战判定: 本机已有重连恢复记录, 忽略观战标志"
                            + "(服务端判定顺序可能有误)。");
                        return;
                    }

                    SpectatorSeat.BeginSpectator();
                }

                return;
            }

            // ── kind=11 职业同步请求(中途回来的人 → 房主) ──
            if (kind == RoleSync.KindRequest)
            {
                RoleSync.OnRequest(playerId);
                return;
            }

            // ── kind=12 职业同步(房主 → 请求者; 其他人收到直接忽略) ──
            if (kind == RoleSync.KindSync)
            {
                RoleSync.OnSync(frame);
                return;
            }

            // ── kind=13 会议中进入: 本场会议坐旧身份的座位(服务端 → 本机) ──
            if (kind == MeetingSeat.KindMeetingSeat)
            {
                MeetingSeat.OnReceived(frame);
                return;
            }

            // ── kind=14 会议代坐: 新身体代坐旧座位(重连者 → 全场; 房主回播生死) ──
            if (kind == MeetingProxy.Kind)
            {
                MeetingProxy.OnReceived(frame);
                return;
            }

            // ── kind=16 重连横幅(服务端 → 全场) ──
            if (kind == RejoinBanner.Kind)
            {
                RejoinBanner.OnFrame(frame);
                return;
            }

            // ── kind=15 代坐投票(代坐者 → 房主) ──
            if (kind == MeetingProxy.VoteKind)
            {
                MeetingProxy.OnVoteFrame(frame);
                return;
            }

            // ── kind=10 会议状态(房主 → 全场) ──
            if (kind == KindMeeting)
            {
                MeetingFlag.OnReceived(frame);
                return;
            }

            // ── kind=8 观战名单(服务端 → 所有人) ──
            if (kind == KindSpectatorRoster)
            {
                SpectatorSeat.ApplyServerRoster(frame);
                return;
            }

            // ── kind=6 死亡名册(服务端 → 所有人) ──
            if (kind == KindGhostRoster)
            {
                DeadRoster.ApplyServerRoster(frame);
                return;
            }

            if (kind != KindRestore)
            {
                // 本插件发出的【上报帧】(kind=1) 会被服务端/模组回显给自己,
                // 这是通道的固有行为, 不是异常 —— 静默丢弃即可。
                return;
            }

            var chunks = new int[frame.Length - 2];
            System.Array.Copy(frame, 2, chunks, 0, chunks.Length);

            ResumeAttemptSeen = true;
            ReconnectPrompt.NoteAttempt("状态包 kind=2");
            NebulaResumeEntry.Log("收到恢复包 playerId=" + playerId + " chunks=" + chunks.Length);
            NebulaResumeEntry.ApplyNebulaState(playerId, chunks);
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("处理接收帧异常: " + e.GetType().Name + " : " + e.Message);
        }
    }

    // ── 序列化 ──
    // 必须与服务端完全一致。服务端用 Hazel 的 reader.ReadInt32() 读,
    // 而 Virial 的 Write(int) 同样是小端, 因此格式天然对齐。

    private static void WriteIntArray(Virial.Utilities.MessageWriter writer, int[] value)
    {
        var arr = value ?? System.Array.Empty<int>();
        writer.Write(arr.Length);
        for (var i = 0; i < arr.Length; i++)
        {
            writer.Write(arr[i]);
        }
    }

    private static int[] ReadIntArray(Virial.Utilities.MessageReader reader)
    {
        var count = reader.ReadInt32();

        // 上限保护: 一个畸形的长度字段不应该让我们分配巨大数组。
        if (count < 0 || count > 4096)
        {
            NebulaResumeEntry.Log("帧长度非法(" + count + "), 丢弃。");
            return System.Array.Empty<int>();
        }

        var arr = new int[count];
        for (var i = 0; i < count; i++)
        {
            arr[i] = reader.ReadInt32();
        }

        return arr;
    }
}
