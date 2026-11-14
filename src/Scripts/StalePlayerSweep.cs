// ============================================================
// StalePlayerSweep.cs — 失效玩家清扫(身体已销毁、却仍被 NoS 当成在场活人)
//
// ▸ 解决什么(日志被 SEHException 刷到几 MB)
//   重连者回来时用的是新身份(新 PlayerId), 旧身份的身体由服务端销毁。
//   NoS 只在 GameData.HandleDisconnect 里把玩家标成 IsDisconnected;
//   旧身份如果没走到这一步, 它的 NoS 对象就会一直留着"在场、存活"的状态,
//   而它引用的 PlayerControl 已经被 Unity 销毁。
//   NoS 读位置用的是直接读原生 Transform 的快速通道(不做存活检查),
//   读到已销毁对象就是原生访问违规 → 每帧刷 SEHException:
//       DanceModule.Update → PlayerModInfo.Position
//       PlayerGetTruePositionPatch.Prefix
//       GameStatistics.Event(会议结束记录事件时, 还会打断流程)
//   这是 SEH 而不是空引用, 帧保护的 Finalizer 也拦不住。
//
// ▸ 怎么做
//   对局进行中, 每帧检查一遍 NoS 的玩家表:
//   "NoS 认为还在场, 但它的 PlayerControl 已经不存在" → 补上 NoS 自己在正常
//   离场时会写的两项: IsDisconnected = true, 状态标签 = 掉线。
//   NoS 对已断开的玩家, 位置一律返回零、IsDead 返回 true, 所有逐帧读取就安全了。
//
//   · 不触发 NoS 的"玩家离场"事件: 这个人并没有真的离开(他已经以新身份回来),
//     触发事件可能连带职业效果(例如恋人殉情), 只做最小的状态修正。
//   · 只看"身体是否还存在"这一事实, 不依赖重连流程的任何中间状态,
//     所以无论旧身份是因为什么原因残留的, 都能被清掉; 正常离场的玩家
//     NoS 早已标好, 这里不会重复动作。
//   · 只在 NoS 对局状态为"进行中"时工作: 结算/回大厅时身体会正常批量销毁,
//     那时不能去改状态标签, 否则结算界面会把所有人显示成掉线。
// ============================================================

using System.Reflection;
using Virial.Game;

namespace NebulaResume;

internal static class StalePlayerSweep
{
    /// <summary>本局已经修正过的 PlayerId(每人只记一次日志)。</summary>
    private static readonly System.Collections.Generic.HashSet<byte> _fixed =
        new System.Collections.Generic.HashSet<byte>();

    private static System.Type? _ngmType;
    private static PropertyInfo? _ngmInstance;
    private static PropertyInfo? _ngmState;
    private static object? _disconnectedTag;
    private static bool _tagResolved;

    internal static void Reset()
    {
        _fixed.Clear();
    }

    /// <summary>由 Entry 每帧调用。</summary>
    internal static void Tick()
    {
        if (ShipStatus.Instance == null)
        {
            if (_fixed.Count > 0)
            {
                _fixed.Clear();
            }

            return;
        }

        if (!IsGameRunning())
        {
            return;
        }

        foreach (var p in Player.AllPlayers)
        {
            if (p == null)
            {
                continue;
            }

            bool disconnected;
            try { disconnected = p.IsDisconnected; } catch { continue; }

            if (disconnected)
            {
                continue;
            }

            if (HasLiveBody(p))
            {
                continue;
            }

            MarkDisconnected(p);
        }
    }

    /// <summary>NoS 对局是否处于"进行中"(Initialized)。</summary>
    private static bool IsGameRunning()
    {
        try
        {
            _ngmType ??= ResumeTransition.Find("Nebula.Game.NebulaGameManager");
            if (_ngmType == null)
            {
                return false;
            }

            _ngmInstance ??= _ngmType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _ngmState ??= _ngmType.GetProperty("GameState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var ngm = _ngmInstance?.GetValue(null);
            if (ngm == null || _ngmState == null)
            {
                return false;
            }

            var state = _ngmState.GetValue(ngm);
            return state != null && state.ToString() == "Initialized";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     该 NoS 玩家引用的 PlayerControl 是否还活着。
    ///     Il2Cpp 的 UnityEngine.Object 比较 null 时会走 Unity 的存活判定,
    ///     对已 Destroy 的对象返回 true(== null), 本身不会碰原生内存。
    /// </summary>
    private static bool HasLiveBody(Player p)
    {
        try
        {
            var t = p.GetType();
            var prop = t.GetProperty("MyControl",
                           BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                       ?? t.GetProperty("VanillaPlayer",
                           BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (prop == null)
            {
                // 拿不到就不判断, 宁可不动。
                return true;
            }

            var pc = prop.GetValue(p) as PlayerControl;
            return pc != null;
        }
        catch
        {
            return true;
        }
    }

    private static void MarkDisconnected(Player p)
    {
        byte pid = 255;
        try { pid = p.PlayerId; } catch { }

        try
        {
            var t = p.GetType();

            var disc = t.GetProperty("IsDisconnected",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (disc != null && disc.CanWrite)
            {
                disc.SetValue(p, true);
            }
            else
            {
                t.GetField("<IsDisconnected>k__BackingField",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(p, true);
            }

            var tag = DisconnectedTag();
            if (tag != null)
            {
                try
                {
                    t.GetField("MyState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(p, tag);
                }
                catch { }
            }

            if (_fixed.Add(pid))
            {
                NebulaResumeEntry.Log("失效玩家清扫: playerId=" + pid
                    + " 的身体已销毁但 NoS 仍当作在场, 已标记为掉线(停止逐帧读取它的位置)。");
            }
        }
        catch (System.Exception e)
        {
            if (_fixed.Add(pid))
            {
                NebulaResumeEntry.Log("失效玩家清扫: playerId=" + pid + " 标记失败: " + e.Message);
            }
        }
    }

    /// <summary>NoS 的"掉线"状态标签 Nebula.Player.PlayerState.Disconnected。</summary>
    private static object? DisconnectedTag()
    {
        if (_tagResolved)
        {
            return _disconnectedTag;
        }

        _tagResolved = true;

        try
        {
            var psType = ResumeTransition.Find("Nebula.Player.PlayerState");
            _disconnectedTag = psType?.GetField("Disconnected",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        }
        catch { }

        return _disconnectedTag;
    }
}
