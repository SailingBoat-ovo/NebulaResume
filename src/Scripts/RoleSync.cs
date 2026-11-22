// ============================================================
// RoleSync.cs — 中途回来的人补齐"全场每个人的职业"
//
// ▸ 现象
//   重连 / 观战回来之后, 在他自己眼里其他所有人都变成了船员
//   (内鬼看不到队友、名牌颜色与职业相关的显示全不对)。
//
// ▸ 原因
//   NOS 只在开局时把每个人的职业下发一次(房主 → 全场)。中途进来的人错过了这一轮,
//   本机重建 NoS 时只能给每个人装一个占位船员 —— 他根本不知道别人是什么职业。
//
// ▸ 做法
//   ① 中途回来的人过渡完成后, 广播一个请求 [11, 自己的 PlayerId]。
//   ② 房主收到后, 把场上其他每个人当前的 职业 / 幽灵职业 / 附加职业 按状态包格式
//      逐人发出: [12, 请求者 PlayerId, 被描述者 PlayerId, ...状态]。
//   ③ 只有请求者本人应用, 而且只在本机生效(不广播) —— 其他客户端上这些玩家的
//      职业状态(冷却、技能次数等)完全不受影响。
//   一段时间内没收到回应会再请求一次(例如请求发出时房主正在换人)。
// ============================================================

namespace NebulaResume;

internal static class RoleSync
{
    internal const int KindRequest = 11;
    internal const int KindSync = 12;

    /// <summary>等多久没收到回应就再请求一次(秒)。</summary>
    private const float RetrySeconds = 6f;

    /// <summary>最多请求几次。</summary>
    private const int MaxAttempts = 3;

    private static bool _pending;
    private static int _attempts;
    private static float _nextRequestAt;
    private static int _received;

    /// <summary>由续局过渡在完成时调用(重连者与观战者都会走到这里)。</summary>
    internal static void RequestAfterTransition()
    {
        _pending = true;
        _attempts = 0;
        _received = 0;
        _nextRequestAt = 0f;
    }

    /// <summary>由 Entry.Tick 调用(1 Hz)。</summary>
    internal static void Tick()
    {
        if (!_pending)
        {
            return;
        }

        if (ShipStatus.Instance == null)
        {
            _pending = false;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextRequestAt)
        {
            return;
        }

        if (_received > 0 || _attempts >= MaxAttempts)
        {
            _pending = false;
            if (_received == 0)
            {
                NebulaResumeEntry.Log("职业同步: 多次请求都没有收到房主回应(房主可能是旧版本插件), 其他玩家仍显示为占位职业。");
            }

            return;
        }

        var lp = PlayerControl.LocalPlayer;
        if (lp == null)
        {
            return;   // 身体还没就位, 下一轮再发
        }

        _attempts++;
        _nextRequestAt = now + RetrySeconds;

        if (NebulaStateWire.SendRaw(new[] { KindRequest, (int)lp.PlayerId }))
        {
            NebulaResumeEntry.Log("职业同步: 已向房主请求全场职业(第 " + _attempts + " 次)。");
        }
    }

    /// <summary>房主: 收到某人的职业同步请求。</summary>
    internal static void OnRequest(int requesterPlayerId)
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost)
            {
                return;
            }

            var sent = 0;
            foreach (var p in Player.AllPlayers)
            {
                if (p == null)
                {
                    continue;
                }

                var pid = ((IArchivedPlayer)p).PlayerId;
                if (pid == requesterPlayerId)
                {
                    continue;   // 请求者自己的职业由状态包恢复, 不在这里覆盖
                }

                var chunks = NebulaStateCodec.Encode(NebulaStateCodec.Capture(p));
                var frame = new int[3 + chunks.Length];
                frame[0] = KindSync;
                frame[1] = requesterPlayerId;
                frame[2] = pid;
                System.Array.Copy(chunks, 0, frame, 3, chunks.Length);

                if (NebulaStateWire.SendRaw(frame))
                {
                    sent++;
                }
            }

            NebulaResumeEntry.Log("职业同步: 已把 " + sent + " 名玩家的职业发给 playerId=" + requesterPlayerId + "。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("职业同步: 房主处理请求失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>收到房主发来的一名玩家的职业。只有请求者本人应用。</summary>
    internal static void OnSync(int[] frame)
    {
        try
        {
            if (frame == null || frame.Length < 4)
            {
                return;
            }

            var lp = PlayerControl.LocalPlayer;
            if (lp == null || frame[1] != lp.PlayerId)
            {
                return;   // 不是发给本机的
            }

            var subjectId = frame[2];
            if (subjectId == lp.PlayerId)
            {
                return;
            }

            var chunks = new int[frame.Length - 3];
            System.Array.Copy(frame, 3, chunks, 0, chunks.Length);

            var snap = NebulaStateCodec.Decode(chunks);
            var subject = Player.GetPlayer((byte)subjectId);

            NebulaStateCodec.ApplyRolesLocal(subject, snap, out var detail);
            _received++;
            NebulaResumeEntry.Log("职业同步: playerId=" + subjectId + " 职业Id=" + snap.Role.Id + " :: " + detail);

            // 会议中进来: 这个人是本机的旧身份 → 本机身上也显示同样的职业。
            //   只在本机生效; 散会后的正式恢复会再按状态包下发一次(全场)。
            if (MeetingSeat.Active && subjectId == MeetingSeat.OldPlayerId)
            {
                var self = Player.GetPlayer(lp.PlayerId);
                NebulaStateCodec.ApplyRolesLocal(self, snap, out var selfDetail);
                NebulaResumeEntry.Log("职业同步: 会议中按旧身份 playerId=" + subjectId + " 显示本机职业 :: " + selfDetail);
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("职业同步: 应用失败: " + e.GetType().Name + " : " + e.Message);
        }
    }
}
