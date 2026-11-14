// ============================================================
// EmergencySync.cs — 中途回来的人对齐紧急会议计时
//
// ▸ 现象
//   重连回来的玩家去按紧急会议按钮, 冷却是从"他重连成功那一刻"重新算的,
//   而不是跟着这一局的实际进度走。
//
// ▸ 原因
//   紧急会议按钮看的是船上的两个计时(ShipStatus.Timer / EmergencyCooldown):
//     · Timer            船生成后累计的时间(开局 15 秒内不能按)
//     · EmergencyCooldown 散会后重新开始的冷却
//   这两个值不走网络同步, 每个客户端各自累计。重连回来的人本机拿到的是一艘
//   【刚生成的船】, 两个计时都从头开始 —— 于是他的冷却跟房间里其他人对不上。
//
// ▸ 做法
//   房主在每秒的会议状态广播里顺带发出自己的两个计时(见 MeetingFlag)。
//   中途回来的人(重连者 / 观战者)在【不在会议中】时, 如果本机计时和房主
//   相差超过 2 秒, 就对齐到房主的值。正常开局的玩家一律不动。
//
// ▸ 诊断
//   所有客户端都会在"会议之外计时被突然重置"时记一行日志(只记录, 不修改),
//   以便判断是否还有别的来源在改它。
// ============================================================

namespace NebulaResume;

internal static class EmergencySync
{
    /// <summary>本机计时与房主相差超过这个值(秒)才对齐。</summary>
    private const float Tolerance = 2f;

    /// <summary>会议/放逐结束后这么久之内不对齐(秒): 散会时各端设置冷却的时刻略有先后。</summary>
    private const float AfterMeetingQuietSeconds = 6f;

    private static float _checkAt;
    private static float _lastMeetingAt = -1000f;

    // 诊断用: 上一帧看到的值
    private static int _shipId;
    private static float _lastTimer = -1f;
    private static float _lastCooldown = -1f;
    private static float _diagLogAt;

    /// <summary>由 Entry.Tick 每帧调用。</summary>
    internal static void Tick()
    {
        try
        {
            var ship = ShipStatus.Instance;
            if (ship == null)
            {
                _shipId = 0;
                _lastTimer = -1f;
                _lastCooldown = -1f;
                _lastMeetingAt = -1000f;
                return;
            }

            var now = UnityEngine.Time.unscaledTime;

            if (MeetingHud.Instance != null || ExileController.Instance != null || MeetingFlag.Active)
            {
                _lastMeetingAt = now;
            }

            var quiet = now - _lastMeetingAt > AfterMeetingQuietSeconds;

            // ── 诊断(所有客户端, 只记录) ──
            var id = ship.GetInstanceID();
            if (id != _shipId)
            {
                _shipId = id;
                _lastTimer = -1f;
                _lastCooldown = -1f;
            }

            var timer = ship.Timer;
            var cooldown = ship.EmergencyCooldown;

            // 开局 30 秒内不诊断: 开局介绍结束时模组会主动设一次冷却, 那是正常行为。
            if (quiet && _lastTimer >= 0f && timer > 30f && now >= _diagLogAt)
            {
                if (timer < _lastTimer - 1f)
                {
                    _diagLogAt = now + 10f;
                    NebulaResumeEntry.Log("紧急计时诊断: 会议之外 ShipStatus.Timer 被重置 "
                        + _lastTimer.ToString("F1") + " → " + timer.ToString("F1") + " 秒。");
                }
                else if (cooldown > _lastCooldown + 1f)
                {
                    _diagLogAt = now + 10f;
                    NebulaResumeEntry.Log("紧急计时诊断: 会议之外 EmergencyCooldown 被重新设置 "
                        + _lastCooldown.ToString("F1") + " → " + cooldown.ToString("F1") + " 秒。");
                }
            }

            // ── 对齐(只有中途回来的人, 每秒一次) ──
            if (quiet && now >= _checkAt)
            {
                _checkAt = now + 1f;
                AlignIfMidJoiner(ship);
                timer = ship.Timer;
                cooldown = ship.EmergencyCooldown;
            }

            _lastTimer = timer;
            _lastCooldown = cooldown;
        }
        catch { }
    }

    private static bool IsMidJoiner()
    {
        try
        {
            return ResumeTransition.Done
                   || SpectatorSeat.AmSpectator
                   || SpawnGuard.IsMidRoundLocal()
                   || NebulaResumeEntry.HasResumedAsReconnecter;
        }
        catch
        {
            return false;
        }
    }

    private static void AlignIfMidJoiner(ShipStatus ship)
    {
        var client = AmongUsClient.Instance;
        if (client == null || client.AmHost || !IsMidJoiner())
        {
            return;
        }

        if (!MeetingFlag.TryGetHostTimers(out var hostTimer, out var hostCooldown))
        {
            return;   // 房主是旧版本(不带计时) / 广播暂时没到
        }

        if (System.Math.Abs(ship.Timer - hostTimer) > Tolerance)
        {
            NebulaResumeEntry.Log("紧急计时: 对齐到房主 Timer " + ship.Timer.ToString("F1")
                + " → " + hostTimer.ToString("F1") + " 秒。");
            ship.Timer = hostTimer;
        }

        // 冷却走完后原版会继续往负数减, 而房主广播时已截到 0 —— 负数一律按 0 比较,
        // 否则会每隔几秒就"对齐"一次(把 -3 改回 0, 再减到 -3 ……)。
        var localCooldown = System.Math.Max(0f, ship.EmergencyCooldown);
        if (System.Math.Abs(localCooldown - hostCooldown) > Tolerance)
        {
            NebulaResumeEntry.Log("紧急计时: 对齐到房主 冷却 " + ship.EmergencyCooldown.ToString("F1")
                + " → " + hostCooldown.ToString("F1") + " 秒。");
            ship.EmergencyCooldown = hostCooldown;
        }
    }
}
