// ============================================================
// SyncAssist.cs — 中途加入者自己补交全房同步回执
//
// ▸ 问题
//   NOS 在会议结算时有三轮"全房握手"(Nebula.Modules.Synchronizer):
//     · CheckExtraVictims  放逐结算后, 等全场回执(含灵魂)
//     · PostMeeting        会议收尾,   等全场回执(含灵魂)
//     · PreSpawnMinigame   散会后"分散位置", 等全场【活人】选完出生点
//   每个客户端都在自己的放逐流程(ExileController 协程)里回执一次,
//   然后等 PlayerControl.AllPlayerControls 里每个人的位都到齐。
//
//   中途加入的人(重连者 / 观战者)如果本机没有跑这段放逐流程 ——
//   会议开始时他还不在房间里 —— 就永远不会回执, 全场卡在等待上。
//
// ▸ 为什么不能由房主"补广播"
//   Synchronizer.SendSync(tag) 只把【调用者自己】标成已回执。
//   所以回执只能由缺席者【自己】发。
//
// ▸ 三条规则(都是为了不干扰正常流程)
//   ① 只在"确实有一轮正在进行"时补交: 本机收到了别人对该标签的回执,
//      或者房主广播"会议/放逐进行中"。平时绝不预先补交 ——
//      预先置上的位会一直留到下一次会议, 让别人在那一轮里不再等本机。
//   ② 本机参与了这一轮会议(本机出现过会议界面/放逐动画/选出生点界面),
//      就完全不插手, 由 NOS 自己的流程回执。
//   ③ 选出生点(PreSpawnMinigame)必须【连同位置一起】回执, 与 NOS 自己的做法一致:
//      散会后 NOS 会按"每个活人的出生点"记一条统计事件, 只有回执没有位置,
//      那一步会因为取不到位置而抛异常, 把散会后的整个收尾流程截断
//      (表现就是散会后回到位置时出错)。灵魂不参与选点, 也就不补交这一项。
// ============================================================

namespace NebulaResume;

internal static class SyncAssist
{
    private static readonly Nebula.Modules.SynchronizeTag[] Tags =
    {
        Nebula.Modules.SynchronizeTag.CheckExtraVictims,
        Nebula.Modules.SynchronizeTag.PostMeeting,
        Nebula.Modules.SynchronizeTag.PreSpawnMinigame,
    };

    /// <summary>每个标签最近一次补交的时刻(用于本机副本的延迟复位)。</summary>
    private static readonly System.Collections.Generic.Dictionary<Nebula.Modules.SynchronizeTag, float> _sentAt =
        new System.Collections.Generic.Dictionary<Nebula.Modules.SynchronizeTag, float>();

    /// <summary>补交后多久复位本机副本(秒)。</summary>
    private const float LocalResetDelay = 3f;

    /// <summary>入场时是否需要检查"进来时别人已经在等本机"。</summary>
    private static bool _entryPending;

    private static float _entryRequestedAt;

    /// <summary>入场检查的有效期(秒): 超过就不再按"入场"处理。</summary>
    private const float EntryWaitSeconds = 30f;

    /// <summary>本机最近一次【亲身参与】会议流程的时刻(出现过会议界面/放逐动画/选点界面)。</summary>
    private static float _participatedAt = -1000f;

    /// <summary>参与一次会议后, 这么久之内都视为"本轮由 NOS 自己回执"(秒)。</summary>
    private const float ParticipationWindow = 120f;

    /// <summary>由续局过渡在完成时调用。</summary>
    internal static void OnEnteredMidRound()
    {
        _entryPending = true;
        _entryRequestedAt = UnityEngine.Time.unscaledTime;
    }

    private static Nebula.Modules.Synchronizer? Get()
    {
        try
        {
            return NebulaAPI.CurrentGame?.GetModule<Nebula.Modules.Synchronizer>();
        }
        catch
        {
            return null;
        }
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

    /// <summary>记录本机是否正在亲身参与会议流程(每帧级别的廉价检查)。</summary>
    private static void NoteParticipation()
    {
        try
        {
            if (MeetingHud.Instance != null || ExileController.Instance != null
                || UnityEngine.Object.FindObjectOfType<Nebula.Behavior.NebulaPreSpawnMinigame>() != null)
            {
                _participatedAt = UnityEngine.Time.unscaledTime;
            }
        }
        catch { }
    }

    private static bool ParticipatingThisRound =>
        UnityEngine.Time.unscaledTime - _participatedAt < ParticipationWindow;

    /// <summary>本机能不能发回执: 有角色、PlayerId 在位图范围内。</summary>
    private static bool CanSend(out PlayerControl? lp)
    {
        lp = PlayerControl.LocalPlayer;
        if (lp == null || lp.Data == null)
        {
            return false;
        }

        // NOS 的回执是 uint 位图, 超过 31 的 PlayerId 本来就表示不了。
        return lp.PlayerId < 32;
    }

    private static bool AmAlive(PlayerControl lp)
    {
        try
        {
            var nos = Virial.Game.Player.GetPlayer(lp.PlayerId);
            if (nos != null)
            {
                return !((Virial.Game.IPlayerlike)nos).IsDead;
            }
        }
        catch { }

        try { return !lp.Data.IsDead; } catch { return false; }
    }

    private static void Send(Nebula.Modules.Synchronizer s, Nebula.Modules.SynchronizeTag tag, PlayerControl lp, string why)
    {
        try
        {
            if (tag == Nebula.Modules.SynchronizeTag.PreSpawnMinigame)
            {
                // 灵魂不参与选出生点(NOS 只等活人), 补交它反而会让位残留。
                if (!AmAlive(lp))
                {
                    return;
                }

                // 位置 + 回执合成一条发出, 与 NOS 自己选完出生点时完全相同。
                var pos = (UnityEngine.Vector2)lp.transform.position;
                Nebula.Modules.CombinedRemoteProcess.CombinedRPC.Invoke(
                    false,
                    Nebula.Game.Statistics.GameStatistics.RpcPoolPosition.GetInvoker(
                        (Nebula.Game.Statistics.GameStatisticsGatherTag.Spawn, lp.PlayerId, pos)),
                    Nebula.Modules.Synchronizer.RpcSync.GetInvoker((tag, lp.PlayerId)));
            }
            else
            {
                s.SendSync(tag);
            }

            _sentAt[tag] = UnityEngine.Time.unscaledTime;
            NebulaResumeEntry.Log("同步回执: 已补交 " + tag + "(" + why + ")。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("同步回执: 补交 " + tag + " 失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>由 Entry.Tick 调用。</summary>
    internal static void Tick()
    {
        if (ShipStatus.Instance == null)
        {
            _sentAt.Clear();
            _entryPending = false;
            _participatedAt = -1000f;
            return;
        }

        NoteParticipation();

        if (!IsMidJoiner() || ParticipatingThisRound)
        {
            return;
        }

        var s = Get();
        if (s == null || !CanSend(out var lp) || lp == null)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        var mine = 1u << lp.PlayerId;

        // 入场检查: 进来时如果房主说"会议/放逐进行中", 别人很可能已经在等本机 → 三项都补交。
        // 不在会议中进来的话, 没有任何一轮在等, 什么都不做(绝不预先置位)。
        if (_entryPending)
        {
            if (now - _entryRequestedAt > EntryWaitSeconds)
            {
                _entryPending = false;
            }
            else if (MeetingFlag.Active)
            {
                _entryPending = false;
                for (var i = 0; i < Tags.Length; i++)
                {
                    Send(s, Tags[i], lp, "入场时有一轮正在进行");
                }
            }
        }

        for (var i = 0; i < Tags.Length; i++)
        {
            var tag = Tags[i];

            try
            {
                // 本机副本到点复位(本机没有协程会替我们复位它)。
                if (_sentAt.TryGetValue(tag, out var at))
                {
                    if (now - at >= LocalResetDelay)
                    {
                        s.ResetSync(tag);
                        _sentAt.Remove(tag);
                    }

                    continue;
                }

                // 有一轮正在进行(本机收到过别人的回执), 自己的位还没置上 → 补交。
                if (!s.sync.TryGetValue(tag, out var val))
                {
                    continue;
                }

                if ((val & ~mine) == 0 || (val & mine) != 0)
                {
                    continue;
                }

                Send(s, tag, lp, "检测到进行中的同步轮次");
            }
            catch { }
        }
    }
}
