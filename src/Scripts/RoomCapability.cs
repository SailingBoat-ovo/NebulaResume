// ============================================================
// RoomCapability.cs — 房主向服务端声明本房间支持哪些能力
//
// ▸ 为什么需要这个
//   对局开始后放行新玩家是有风险的操作:
//     · 原版房间里放进来一个人, 他没有身体、没有职业, 只会卡住;
//     · 没装本插件的房间更不该被改动原有行为。
//
//   所以不能让服务端"只要开关打开就无脑放行"。
//   正确做法是: 房主先声明"这个房间装了插件、支持观战", 服务端才敢放行。
//
// ▸ 协议
//   kind=9  房主 → 服务端   [9, 协议版本, 能力标志, 观战上限]
//
//   能力标志按位:
//     bit 0 = 支持断线重连
//     bit 1 = 支持对局中放行观战者
//
// ▸ 发送时机
//   房主在大厅阶段就要开始上报 —— 服务端必须在【有人尝试加入之前】
//   就知道这个房间支不支持观战。开局后继续周期上报, 防止丢包。
// ============================================================

namespace NebulaResume;

internal static class RoomCapability
{
    /// <summary>协议版本。与服务端约定, 不一致时服务端应拒绝放行。</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>能力标志: 支持断线重连。</summary>
    internal const int FlagReconnect = 1 << 0;

    /// <summary>能力标志: 支持对局中放行观战者。</summary>
    internal const int FlagSpectator = 1 << 1;

    /// <summary>
    ///     会议中进入: 有保槽的重连者会议期间也能进房, 以旧身份的座位参加这场会议,
    ///     散会后再正常续局。(1 &lt;&lt; 2 曾被一个已撤回的试验用过, 故意跳过。)
    /// </summary>
    internal const int FlagMeetingJoin = 1 << 3;

    /// <summary>上报间隔(秒)。</summary>
    private const float Interval = 4f;

    private static float _nextAt;
    private static int _lastSent = -1;

    internal static void Reset()
    {
        _nextAt = 0f;
        _lastSent = -1;
    }

    /// <summary>由 Entry.Tick 每帧调用。只有房主会上报。</summary>
    internal static void Tick()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost)
            {
                return;
            }

            // 必须已经在房间里(大厅或对局都行), 但不在主菜单。
            var inRoom = LobbyBehaviour.Instance != null
                         || ShipStatus.Instance != null
                         || client.GameState != InnerNetClient.GameStates.NotJoined;

            if (!inRoom)
            {
                _nextAt = 0f;
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            if (now < _nextAt)
            {
                return;
            }

            _nextAt = now + Interval;

            // 本房间支持的能力: 重连, 以及会议中进入(散会后再续局, 见 MeetingSeat)。
            //   设置里关掉「允许断线重连」→ 两者都不声明, 服务端就不给掉线的人保槽。
            var reconnectOn = true;
            try { reconnectOn = ResumeOptions.ReconnectAllowed; } catch { }
            var meetingJoinOn = reconnectOn;
            try { meetingJoinOn = ResumeOptions.MeetingJoinAllowed; } catch { }
            var flags = (reconnectOn ? FlagReconnect : 0) | (meetingJoinOn ? FlagMeetingJoin : 0);

            // 观战: 只有模组的观战开关打开时才声明。
            try
            {
                if (ResumeOptions.SpectatorEnabled)
                {
                    flags |= FlagSpectator;
                }
            }
            catch { }

            var spectatorMax = 0;
            try { spectatorMax = ResumeOptions.SpectatorLimit; } catch { }

            // 能力没变化时不重复刷日志, 但仍然要发(自愈)。
            var signature = flags * 100 + spectatorMax;

            // ⚠️ 必须用 SendRaw, 不能用 Send。
            //   Send 要求 GameState == Started, 而房主在大厅时是 Joined ——
            //   大厅阶段的上报会被静默丢弃, 服务端永远收不到能力声明。
            var sent = NebulaStateWire.SendRaw(new[]
            {
                NebulaStateWire.KindCapability,
                ProtocolVersion,
                flags,
                spectatorMax,
            });

            if (signature != _lastSent)
            {
                if (sent)
                {
                    _lastSent = signature;
                    NebulaResumeEntry.Log("房间能力: 已声明 重连=" + (reconnectOn ? "是" : "否")
                        + " 会议中进入=" + (meetingJoinOn ? "是" : "否") + " 观战="
                        + (((flags & FlagSpectator) != 0) ? "是" : "否")
                        + " 上限=" + spectatorMax);
                }
                else
                {
                    // 角色还没就绪时发不出去, 保持 _lastSent 不变, 下一轮重试。
                    NebulaResumeEntry.Log("房间能力: 本机角色未就绪, 本轮未发出, 稍后重试。");
                }
            }
        }
        catch { }
    }
}
