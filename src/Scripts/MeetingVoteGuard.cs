// ============================================================
// MeetingVoteGuard.cs — 会议期间：观战者与死者不参与投票
//
// ▸ 为什么要额外做一层
//   香草本身就不让死者投票(IsDead 的投票格是禁用的), 所以理论上
//   重连后仍为死亡、以及观战职业, 都不会被算进投票。
//
//   但模组内部维护了一个独立的投票权限掩码, 并且它会在会议开始时
//   重新初始化成"所有人可投"。观战者的状态是中途加进来的,
//   如果这个掩码没同步排除他, 就会出现"他有投票权但不投"的情况 ——
//   而香草的会议结束判定要等所有人投完, 于是会议卡到超时。
//
//   所以这里在每个会议期间主动把本机的投票权限关掉:
//   只在他确实是死者/观战者时生效, 正常玩家完全不受影响。
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class MeetingVoteGuard
{
    /// <summary>本机是否应该被禁止投票。</summary>
    private static bool ShouldBlockVote()
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.Data == null)
            {
                return false;
            }

            // 死者(含重连后仍为死亡)与观战者都不投票
            if (lp.Data.IsDead)
            {
                return true;
            }

            // 观战职业在生还时也不该投票(理论上观战者恒为灵魂, 这里保险)
            return SpectatorSeat.AmSpectator;
        }
        catch
        {
            return false;
        }
    }

    private static float _nextAt;
    private static bool _logged;

    internal static void Reset()
    {
        _nextAt = 0f;
        _logged = false;
    }

    /// <summary>由 Entry.Tick 每帧调用。</summary>
    internal static void Tick()
    {
        try
        {
            if (MeetingHud.Instance == null)
            {
                _nextAt = 0f;
                _logged = false;
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            if (now < _nextAt)
            {
                return;
            }

            _nextAt = now + 0.5f;

            if (!ShouldBlockVote())
            {
                return;
            }

            if (TryBlockVote() && !_logged)
            {
                _logged = true;
                NebulaResumeEntry.Log("会议: 本机是死者/观战者, 已关闭投票权限(避免卡投票)。");
            }
        }
        catch { }
    }

    /// <summary>把模组的投票权限关掉。返回是否成功。</summary>
    private static bool TryBlockVote()
    {
        try
        {
            var t = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension")
                    ?? ResumeTransition.Find("Nebula.Patches.MeetingHudExtension");

            if (t == null)
            {
                return false;
            }

            var prop = t.GetProperty("CanVote",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (prop == null || !prop.CanWrite)
            {
                return false;
            }

            prop.SetValue(null, false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
