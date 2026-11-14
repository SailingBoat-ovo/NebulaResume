// ============================================================
// FrameGuard.cs — 重连期间的帧级异常保护(Finalizer)
//
// ▸ 为什么还需要它
//   仅靠"生成即注册守卫"把 NoS 对象补出来并不够: 新身体刚生成的一小段时间里,
//   NoS 的玩家对象可能仍在半初始化(外观/角色字段尚未全部就绪), 而
//   PlayerControl.FixedUpdate 上的 NoS 补丁是【每帧直接解引用】的:
//       PlayerModInfo.Update → CalcSize → Component.get_transform()
//   该窗口期内每个客户端会刷出大量 NullReferenceException, 导致卡顿甚至掉线。
//
// ▸ 做法(最小侵入)
//   只在【本局确实发生过重连/中途生成】时才给这两处挂 Finalizer, 把空引用吞掉;
//   其余情况异常原样抛出, 不影响正常排错。
//     ① PlayerControl.FixedUpdate
//     ② Nebula.Player.PlayerModInfo.CalcSize / UpdateModulators
// ============================================================

using System.Reflection;
using HarmonyLib;

namespace NebulaResume;

internal static class FrameGuard
{
    private static bool _armed;
    private static float _logAt;
    private static int _swallowed;

    /// <summary>本局是否进入"重连保护期"(发生过中途生成/重连)。</summary>
    internal static bool Armed => _armed;

    internal static void Arm()
    {
        _armed = true;
    }

    internal static void Reset()
    {
        _armed = false;
        _swallowed = 0;
    }

    internal static void CountSwallow()
    {
        _swallowed++;

        var now = UnityEngine.Time.unscaledTime;
        if (now < _logAt)
        {
            return;
        }

        _logAt = now + 5f;
        NebulaResumeEntry.Log("帧保护: 已吞掉 " + _swallowed + " 次重连窗口内的空引用(其余时间不受影响)。");
    }

    /// <summary>由 Entry 安装。</summary>
    internal static void Install(Harmony harmony)
    {
        // ① PlayerControl.FixedUpdate
        try
        {
            var m = AccessTools.Method(typeof(PlayerControl), "FixedUpdate");
            if (m != null)
            {
                harmony.Patch(m, finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(FrameGuard), nameof(PlayerFixedUpdateFinalizer))));
                NebulaResumeEntry.Log("帧保护: 已挂 PlayerControl.FixedUpdate Finalizer。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("帧保护: 挂 FixedUpdate 失败: " + e.Message);
        }

        // ①-b 会议投票格外观(重连者缺外观数据时会抛空引用, 导致投票格初始化中断)
        try
        {
            var m = AccessTools.Method(typeof(PlayerVoteArea), "SetCosmetics");
            if (m != null)
            {
                harmony.Patch(m, finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(FrameGuard), nameof(CalcSizeFinalizer))));
                NebulaResumeEntry.Log("帧保护: 已挂 PlayerVoteArea.SetCosmetics Finalizer。");
            }
            else
            {
                NebulaResumeEntry.Log("帧保护: 未找到 PlayerVoteArea.SetCosmetics。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("帧保护: 挂 SetCosmetics 失败: " + e.Message);
        }

        // ①-c 会议投票格排序(NOS 补丁里对 GamePlayer 解引用不判空)
        try
        {
            var t = ResumeTransition.Find("Nebula.Patches.MeetingModRpc");
            var m = t?.GetMethod("SortVotingArea",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (m != null)
            {
                harmony.Patch(m, finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(FrameGuard), nameof(CalcSizeFinalizer))));
                NebulaResumeEntry.Log("帧保护: 已挂 MeetingModRpc.SortVotingArea Finalizer。");
            }
            else
            {
                NebulaResumeEntry.Log("帧保护: 未找到 SortVotingArea。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("帧保护: 挂 SortVotingArea 失败: " + e.Message);
        }

        // ①-d 会议开始时的贴纸展示器初始化。
        //   该补丁遍历全场玩家, 对每个人的投票格调用 GetMeetingShower。
        //   若某玩家在会议 UI 里没有投票格(中途重连者可能出现), 取到 null 会抛空引用,
        //   中断后续玩家的初始化 —— 表现是部分投票格信息缺失。
        try
        {
            var t = ResumeTransition.Find("Nebula.Patches.MeetingStartPatch");

            if (t != null)
            {
                var m = t.GetMethod("Postfix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (m != null)
                {
                    harmony.Patch(m, finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(FrameGuard), nameof(CalcSizeFinalizer))));
                    NebulaResumeEntry.Log("帧保护: 已挂 MeetingStartPatch.Postfix Finalizer。");
                }
                else
                {
                    NebulaResumeEntry.Log("帧保护: 未找到 MeetingStartPatch.Postfix。");
                }
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("帧保护: 挂 MeetingStartPatch 失败: " + e.Message);
        }

        // ② NoS 的 CalcSize / UpdateModulators(内部类型, 用反射找方法)
        try
        {
            var t = ResumeTransition.Find("Nebula.Player.PlayerModInfo");
            if (t != null)
            {
                var calc = t.GetMethod("CalcSize",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(float) }, null);

                if (calc == null)
                {
                    NebulaResumeEntry.Log("帧保护: 未找到 PlayerModInfo.CalcSize(签名变了?), 跳过。");
                }

                if (calc != null)
                {
                    harmony.Patch(calc, finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(FrameGuard), nameof(CalcSizeFinalizer))));
                    NebulaResumeEntry.Log("帧保护: 已挂 PlayerModInfo.CalcSize Finalizer。");
                }

                var upd = t.GetMethod("UpdateModulators",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(float) }, null);

                if (upd == null)
                {
                    NebulaResumeEntry.Log("帧保护: 未找到 PlayerModInfo.UpdateModulators(签名变了?), 跳过。");
                }

                if (upd != null)
                {
                    harmony.Patch(upd, finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(FrameGuard), nameof(CalcSizeFinalizer))));
                    NebulaResumeEntry.Log("帧保护: 已挂 PlayerModInfo.UpdateModulators Finalizer。");
                }
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("帧保护: 挂 NoS 方法失败: " + e.Message);
        }
    }

    /// <summary>PlayerControl.FixedUpdate 的 Finalizer。</summary>
    private static System.Exception? PlayerFixedUpdateFinalizer(System.Exception __exception)
    {
        if (__exception == null)
        {
            return null;
        }

        if (!_armed)
        {
            return __exception;   // 非重连期: 原样抛出, 不掩盖真实问题
        }

        CountSwallow();
        return null;
    }

    /// <summary>NoS 两个方法的 Finalizer。</summary>
    private static System.Exception? CalcSizeFinalizer(System.Exception __exception)
    {
        if (__exception == null)
        {
            return null;
        }

        if (!_armed)
        {
            return __exception;
        }

        CountSwallow();
        return null;
    }
}
