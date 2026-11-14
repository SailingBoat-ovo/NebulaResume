// ============================================================
// LegacyTaskCount.cs — 通知 NOS 任务数量(反射)
//
// ▸ 为什么需要
//   NOS 在 NetworkedPlayerInfo.SetTasks 上挂了 Prefix, 对本机玩家会
//   走自己的流程并调用 RPCEventInvoker.SetTasks(playerId, 实际数量, ...)。
//   我们绕过拦截直接建任务时, 必须自己补这一步, 否则 NOS 认为这个人
//   没有任务(任务栏计数、胜负判定里的任务进度都不对)。
//
// ▸ 对应源码
//     RPCEventInvoker.SetTasks(byte playerId, int allTasks, bool isCrewmateTask, bool isInfiniteQuota)
//     RPC.cs:1072
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class LegacyTaskCount
{
    private static System.Type? _invokerType;
    private static bool _searched;

    private static System.Type? InvokerType()
    {
        if (_searched)
        {
            return _invokerType;
        }

        _searched = true;

        try
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("Nebula.RPC.RPCEventInvoker", false)
                            ?? asm.GetType("Nebula.RPCEventInvoker", false);

                    if (t != null)
                    {
                        _invokerType = t;
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }

        return _invokerType;
    }

    internal static void Notify(byte playerId, int count)
    {
        try
        {
            var t = InvokerType();
            if (t == null)
            {
                return;
            }

            var m = t.GetMethod("SetTasks",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(byte), typeof(int), typeof(bool), typeof(bool) },
                null);

            m?.Invoke(null, new object[] { playerId, count, true, false });
        }
        catch { }
    }
}
