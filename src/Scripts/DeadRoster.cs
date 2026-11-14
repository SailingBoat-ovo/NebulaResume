// ============================================================
// DeadRoster.cs — 死亡名册(全局广播 + 本机对账)
//
// ▸ 它解决什么
//   重连者回来之后, 必须让【全场每一台客户端】都认为这个人是死者:
//     · 别人眼里他不是活人实体, 也不能被击杀;
//     · 会议里他有死亡标记, 不占投票位;
//     · 他能看到死者频道、以灵魂状态观察。
//
// ▸ 为什么不能只靠房主写数据
//   运行日志可以证明: 房主把 NetworkedPlayerInfo.IsDead 置 true 并 MarkDirty()
//   之后, 其他客户端那份数据【仍然是 false】(其他窗口从未打印过"死亡镜像"),
//   也就是说这条同步路径在当前服务端配置下不可用。
//
// ▸ 本方案
//   走我们自己的 RPC 通道(与 kind 1/2/3/4 同一个), 由服务端做【全量名册广播】:
//
//       kind=5  重连者 → 服务端       [5, playerId]         "我是死者, 请记入名册"
//       kind=6  服务端 → 所有人       [6, 人数, clientId…]  "这些人必须显示为死者"
//
//   选【全量名册】而不是"某人变成鬼了"这种事件, 是因为:
//     · 幂等: 收到几次都一样, 不会算重;
//     · 自愈: 丢一包不影响最终状态, 下一包就是完整名单;
//     · 后加入的客户端也能一次拿到完整状态, 不需要补历史。
//
// ▸ 收到名册之后做什么
//   本机对账(每 250ms): 名册里的人, 只要本机 NoS 层还不是死的, 就做一次
//   本地灵魂化(香草层 + NoS 层一起写), 并记账避免重复。
//   这里【不依赖香草数据是死是活】—— 名册本身就是权威。
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class DeadRoster
{
    /// <summary>对账节流: 250ms 一轮。</summary>
    private const float TickInterval = 0.25f;

    /// <summary>名册上报间隔(重连者周期性重申自己的死者身份)。</summary>
    private const float ReportInterval = 5f;

    /// <summary>服务端下发/本机确认的"必须显示为死者"的 clientId 集合。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _roster =
        new System.Collections.Generic.HashSet<int>();

    /// <summary>已在本机完成灵魂化的 clientId(避免每轮重复走死亡流程)。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _localized =
        new System.Collections.Generic.HashSet<int>();

    private static float _tickAt;
    private static float _reportAt;
    private static float _notFoundLogAt;
    private static bool _isResumed;

    internal static int Count => _roster.Count;

    internal static void Reset()
    {
        _roster.Clear();
        _localized.Clear();
        _tickAt = 0f;
        _reportAt = 0f;
        _notFoundLogAt = 0f;
        _isResumed = false;
    }

    /// <summary>
    ///     标记本机是"重连回来的玩家"。
    ///
    ///   ⚠️ 只有【掉线前已死亡】的重连者才需要周期上报死者身份;
    ///      掉线前活着的重连者要恢复成活人, 绝不能上报(否则会被全场当鬼处理)。
    /// </summary>
    internal static void MarkSelfResumed(bool isDead)
    {
        _isResumed = isDead;

        if (isDead)
        {
            _reportAt = 0f;   // 下一次 Tick 立刻上报
            NebulaResumeEntry.Log("死亡名册: 本机掉线前已死亡, 开始周期上报。");
        }
        else
        {
            NebulaResumeEntry.Log("死亡名册: 本机掉线前存活, 不上报死者身份。");
        }
    }

    /// <summary>观战者: 无条件固定为灵魂(不依赖服务端是否把名册发回来)。</summary>
    internal static void MarkSelfSpectator()
    {
        _isResumed = true;
        _reportAt = 0f;
        NebulaResumeEntry.Log("死亡名册: 本机是观战者, 固定为灵魂并周期上报。");
    }

    /// <summary>本地登记一个必须显示为死者的人(房主收到补生成指令时用)。</summary>
    internal static void Require(int clientId, string why)
    {
        if (clientId < 0)
        {
            return;
        }

        if (_roster.Add(clientId))
        {
            NebulaResumeEntry.Log("死亡名册: 登记 clientId=" + clientId + " (" + why + ")。");
        }
    }

    /// <summary>
    ///     把【本机玩家】登记为必须显示为死者。
    ///     用于观战者: 它一进来就该是灵魂, 不必等服务端把名册发回来。
    /// </summary>
    internal static void RequireSelfAsGhost(string why)
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null)
            {
                return;
            }

            var cid = -1;
            try { cid = lp.OwnerId; } catch { }
            if (cid < 0)
            {
                try { cid = lp.Data != null ? lp.Data.ClientId : -1; } catch { }
            }

            if (cid >= 0)
            {
                Require(cid, why);
            }
        }
        catch { }
    }

    /// <summary>收到服务端下发的全量名册(kind=6)。</summary>
    internal static void ApplyServerRoster(int[] frame)
    {
        if (frame == null || frame.Length < 2)
        {
            return;
        }

        var count = frame[1];
        if (count < 0 || count > 64 || frame.Length < 2 + count)
        {
            NebulaResumeEntry.Log("死亡名册: 人数非法(" + count + "), 丢弃。");
            return;
        }

        var before = _roster.Count;

        // 全量替换: 服务端说了算
        _roster.Clear();
        for (var i = 0; i < count; i++)
        {
            var cid = frame[2 + i];
            if (cid >= 0)
            {
                _roster.Add(cid);
            }
        }

        // 已经被移出名册的人(例如复活), 允许下次重新灵魂化
        _localized.RemoveWhere(c => !_roster.Contains(c));

        if (before != _roster.Count || count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("死亡名册: 收到服务端全量名册 ").Append(count).Append(" 人 [");
            var first = true;
            foreach (var c in _roster)
            {
                if (!first) sb.Append(',');
                sb.Append(c);
                first = false;
            }

            sb.Append("]。");
            NebulaResumeEntry.Log(sb.ToString());
        }
    }

    /// <summary>由 Entry.Tick 每帧调用。</summary>
    internal static void Tick()
    {
        try
        {
            var now = UnityEngine.Time.unscaledTime;

            // ── ① 重连者: 周期性向服务端重申"我是死者" ──
            //   服务端据此维护名册并全量广播。周期性重发是为了丢包自愈。
            if (_isResumed && now >= _reportAt)
            {
                _reportAt = now + ReportInterval;

                if (PlayerControl.LocalPlayer != null)
                {
                    NebulaStateWire.Send(new[]
                    {
                        NebulaStateWire.KindGhostReport,
                        (int)PlayerControl.LocalPlayer.PlayerId,
                    });
                }
            }

            // ── ② 对账: 名册里的人在本机必须是死者 ──
            //
            //   名册来源有两类:
            //     · 服务端广播的观战者 / 掉线前已死的重连者(kind=6)
            //     · 房主收到补生成指令时的本地登记
            //   掉线前活着的重连者【不会】进名册 —— 它们要恢复成活人。
            if (_roster.Count == 0)
            {
                return;
            }

            if (ShipStatus.Instance == null && !SpectatorSeat.AmSpectator)
            {
                // 没有船场景就什么都不做(RoomGuard/FindByClientId 依赖对局对象)。
                // 但【本机是观战者】时例外: 中途加入的人可能迟迟没有船场景,
                // 而全房都在等他的资料回执 —— 这时必须照常把他自己灵魂化,
                // 否则整房会卡在"正在加载信息"的同步标签上。
                return;
            }

            if (now < _tickAt)
            {
                return;
            }

            _tickAt = now + TickInterval;

            var amHost = false;
            try { amHost = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost; } catch { }

            // ── 清理: 名册里已经离场/不存在的 clientId ──
            //   clientId 会被服务端重用, 残留条目会让 FindByClientId 命中错误的人。
            try
            {
                System.Collections.Generic.List<int>? stale = null;

                foreach (var cid in _roster)
                {
                    if (FindByClientId(cid) == null)
                    {
                        (stale ??= new System.Collections.Generic.List<int>()).Add(cid);
                    }
                }

                if (stale != null)
                {
                    foreach (var cid in stale)
                    {
                        _roster.Remove(cid);
                        _localized.Remove(cid);
                    }

                    NebulaResumeEntry.Log("死亡名册: 已清理 " + stale.Count
                        + " 个失效条目(玩家已离场)。");
                }
            }
            catch { }

            foreach (var clientId in _roster)
            {
                var pc = FindByClientId(clientId);

                if (pc == null || pc.Data == null)
                {
                    // 身体还没生成 → 下轮再试。每 5 秒报一次, 方便定位到底卡在哪。
                    if (now >= _notFoundLogAt)
                    {
                        _notFoundLogAt = now + 5f;
                        NebulaResumeEntry.Log("死亡名册: 暂时找不到 clientId=" + clientId
                            + " 的身体, 继续等待(名册人数=" + _roster.Count + ")。");
                    }

                    continue;
                }

                // ── ②-1 房主: 维护香草层的权威生死态(数据正确性) ──
                if (amHost)
                {
                    try
                    {
                        if (!pc.Data.IsDead)
                        {
                            pc.Data.IsDead = true;
                            pc.Data.MarkDirty();
                            NebulaResumeEntry.Log("权威侧: 已把 clientId=" + clientId
                                + " 的生死态置为死亡并标脏。");
                        }
                    }
                    catch { }
                }

                // ── ②-2 所有客户端: 维护本机表现层 ──
                var nosP = Virial.Game.Player.GetPlayer(pc.PlayerId);

                if (nosP == null)
                {
                    continue;   // 交给生成守卫先把它注册进本机 NoS
                }

                var nosDead = false;
                try
                {
                    var prop = nosP.GetType().GetProperty("IsDead",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    nosDead = prop?.GetValue(nosP) is bool b && b;
                }
                catch { }

                // 只做一次; 但如果后来被改回活人(状态被覆盖), 这里会再次执行。
                if (nosDead && _localized.Contains(clientId))
                {
                    continue;
                }

                try
                {
                    // ★ 观战者必须先关掉「幽灵职业」分配, 再变鬼:
                    //   NOS 的死亡流程会给死者随机分配吵闹鬼一类幽灵职业,
                    //   而观战席不该带任何能影响对局的能力(见 SpectatorSeat.ArmNoGhostRole)。
                    try
                    {
                        if (SpectatorSeat.AmSpectator)
                        {
                            var self = PlayerControl.LocalPlayer;
                            if (self != null && pc == self)
                            {
                                SpectatorSeat.ArmNoGhostRoleFor(nosP);
                            }
                        }
                    }
                    catch { }

                    NebulaStateCodec.MakeGhostLocal(pc, nosP, true);
                    _localized.Add(clientId);
                    NebulaResumeEntry.Log("表现侧: clientId=" + clientId
                        + "(id=" + pc.PlayerId + ") 已在本机完成灵魂化。");
                }
                catch (System.Exception e)
                {
                    NebulaResumeEntry.Log("表现侧: clientId=" + clientId + " 灵魂化失败: " + e.Message);
                }
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("死亡名册 Tick 异常: " + e.Message);
        }
    }

    /// <summary>
    ///     按 clientId 找玩家实体。
    ///     三种匹配依次尝试: 归属(OwnerId) → 资料里的 ClientId → 放弃。
    ///   ⚠️ 新生成的身体, 资料里的 ClientId 可能还是 0, 所以归属匹配是首选。
    /// </summary>
    private static PlayerControl? FindByClientId(int clientId)
    {
        var pcs = PlayerControl.AllPlayerControls;
        if (pcs == null)
        {
            return null;
        }

        PlayerControl? byData = null;

        for (var i = 0; i < pcs.Count; i++)
        {
            var pc = pcs[i];
            if (pc == null || pc.Data == null)
            {
                continue;
            }

            try
            {
                if (pc.OwnerId == clientId)
                {
                    return pc;
                }
            }
            catch { }

            if (byData == null)
            {
                try
                {
                    if (pc.Data.ClientId == clientId)
                    {
                        byData = pc;
                    }
                }
                catch { }
            }
        }

        return byData;
    }
}
