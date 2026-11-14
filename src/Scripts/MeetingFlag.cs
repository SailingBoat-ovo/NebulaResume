// ============================================================
// MeetingFlag.cs — 会议状态同步(房主 → 全场)
//
// ▸ 为什么客户端自己判断不了"现在是不是在开会"
//   会议界面是在【会议开始那一刻】由客户端自己按 RpcStartMeeting 建出来的。
//   一个中途重连回来的人没有收到那条 RPC, 本机的 MeetingHud 根本不存在 ——
//   `MeetingHud.Instance != null` 对他永远是 false。
//   所以"会议禁止重连"如果只查本机, 在真实场景里 100% 不会触发。
//
// ▸ 本文件做什么
//   房主每 1 秒向全场广播一次当前会议状态(含放逐动画):
//       [KindMeeting, 1/0]
//   这条广播走的是现成的 RPC 通道, 服务端只做转发; 已加入房间的客户端即使
//   还没有角色身体也收得到(与能力声明 kind=9 同一条路)。
//
// ▸ 消费方
//   ResumeTransition 用它做"会议期间拒绝重连"的判定;
//   因为广播是周期性的, 刚进来的人最多等一个周期(1 秒)就能拿到状态。
// ============================================================

namespace NebulaResume;

internal static class MeetingFlag
{
    /// <summary>房主广播间隔(秒)。1 秒已经足够, 且几乎不占流量。</summary>
    private const float SendInterval = 1f;

    /// <summary>
    ///     信息有效期(秒)。超过这么久没收到房主的广播就当作"不知道"
    ///     —— 例如房主掉线了。宁可按原逻辑继续, 也不要一直卡着不重连。
    /// </summary>
    private const float StaleSeconds = 3f;

    private static bool _known;
    private static bool _active;
    private static float _seenAt;
    private static float _sendAt;

    internal static void Reset()
    {
        _known = false;
        _active = false;
        _seenAt = 0f;
        _sendAt = 0f;
        _hasHostTimers = false;
    }

    /// <summary>手上是否有一份仍然新鲜的状态。</summary>
    internal static bool HasFreshInfo
    {
        get
        {
            if (!_known)
            {
                return false;
            }

            return UnityEngine.Time.unscaledTime - _seenAt <= StaleSeconds;
        }
    }

    /// <summary>现在是否处在会议(或放逐动画)中。</summary>
    internal static bool Active
    {
        get
        {
            // 自己就在会议里 → 本机状态比广播更直接, 优先采用。
            if (MeetingHud.Instance != null || ExileController.Instance != null)
            {
                return true;
            }

            return HasFreshInfo && _active;
        }
    }

    /// <summary>把本机观察到的会议状态也记成一次"已知"。</summary>
    internal static void NoteLocal()
    {
        if (MeetingHud.Instance != null || ExileController.Instance != null)
        {
            _known = true;
            _active = true;
            _seenAt = UnityEngine.Time.unscaledTime;
        }
    }

    /// <summary>由 Entry.Tick 每帧调用: 房主周期性广播状态, 其余客户端不做额外动作。</summary>
    internal static void Tick()
    {
        var now = UnityEngine.Time.unscaledTime;

        // 已经不在对局里 → 清掉, 避免把上一局的"会议中"带进下一次重连。
        if (ShipStatus.Instance == null)
        {
            if (_known || _active)
            {
                Reset();
            }

            return;
        }

        if (now < _sendAt)
        {
            return;
        }

        _sendAt = now + SendInterval;

        AmongUsClient? client = null;
        try { client = AmongUsClient.Instance; } catch { }

        if (client == null)
        {
            return;
        }

        var amHost = false;
        try { amHost = client.AmHost; } catch { }
        if (!amHost)
        {
            return;
        }

        var active = MeetingHud.Instance != null || ExileController.Instance != null;

        // 顺带带上房主的两个紧急会议计时(毫秒), 供中途回来的人对齐(见 EmergencySync)。
        //   旧版本客户端只读前两个 int, 多出来的会被忽略, 兼容。
        var timerMs = 0;
        var cooldownMs = 0;
        try
        {
            var ship = ShipStatus.Instance;
            timerMs = (int)(System.Math.Max(0f, ship.Timer) * 1000f);
            cooldownMs = (int)(System.Math.Max(0f, ship.EmergencyCooldown) * 1000f);
        }
        catch { }

        // 状态没变也要按周期发: 刚进来的客户端靠下一次广播才能知道现状。
        NebulaStateWire.SendRaw(new[]
        {
            NebulaStateWire.KindMeeting,
            active ? 1 : 0,
            timerMs,
            cooldownMs,
        });
    }

    // ── 房主的紧急会议计时(最近一次广播) ──
    private static bool _hasHostTimers;
    private static float _hostTimer;
    private static float _hostCooldown;
    private static float _hostTimersAt;

    /// <summary>
    ///     取房主最近一次广播的紧急会议计时, 并按广播后经过的时间推算到"现在"。
    ///     超过 2.5 秒没收到新的广播就当作不知道。
    /// </summary>
    internal static bool TryGetHostTimers(out float timer, out float cooldown)
    {
        timer = 0f;
        cooldown = 0f;

        if (!_hasHostTimers)
        {
            return false;
        }

        var age = UnityEngine.Time.unscaledTime - _hostTimersAt;
        if (age > 2.5f)
        {
            return false;
        }

        timer = _hostTimer + age;
        cooldown = System.Math.Max(0f, _hostCooldown - age);
        return true;
    }

    /// <summary>收到房主的广播。</summary>
    internal static void OnReceived(int[] frame)
    {
        if (frame == null || frame.Length < 2)
        {
            return;
        }

        _known = true;
        _active = frame[1] != 0;
        _seenAt = UnityEngine.Time.unscaledTime;

        // 新版房主会多带两个计时(旧版房主没有, 就不更新)。
        if (frame.Length >= 4)
        {
            _hasHostTimers = true;
            _hostTimer = frame[2] / 1000f;
            _hostCooldown = frame[3] / 1000f;
            _hostTimersAt = _seenAt;
        }
    }
}