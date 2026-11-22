// ============================================================
// SpawnGuard.cs — 生成即注册守卫
//
// ▸ 解决什么(未注册窗口内每帧抛空引用, 可累计上万条)
//   重连者的身体是中途生成的, 会广播到每一台客户端。各机的"NoS 层"
//   原本没有任何时机知道这个新玩家 —— 于是出现一个窗口:
//       vanilla 有这个人, 本机 NoS 没有
//   而 NoS v9 的 PlayerUpdatePatch(挂在 PlayerControl.FixedUpdate 上)每帧
//   直接解引用该玩家的 NoS 对象(不判空) → 每帧抛 NullReferenceException,
//   整个游戏卡住、其他人陆续掉线, 会议界面也崩。
//
// ▸ 怎么做
//   每帧(人数变化时才真正扫描)比对 AllPlayerControls 与本机 NoS:
//   发现"vanilla 有、NoS 没有"的人 → 【当帧】注册进 NoS 并装一个占位职业,
//   窗口立刻闭合。
//
//   · 占位职业只为让 NoS 对象完整(它必须有 Role 才能被读出 IsImpostor 等);
//     真实身份仍由状态包/房主同步负责。
//   · 注册可能在前一两帧失败(Data.DefaultOutfit 等还没链接好)→ 250ms 退避重试。
//   · 成本: 人数不变时每帧只有一次整数比较; 仅在真的有新玩家时全量扫一轮。
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class SpawnGuard
{
    /// <summary>
    ///     本轮开局的玩家名单(基准)。
    ///
    ///   ⚠️ 关键: 守卫【绝不能】碰开局时在场的玩家 —— NOS 会自己把他们的
    ///   真实职业/状态注册好, 我们抢先注册并用占位职业覆盖, 会把全场身份毁掉
    ///   (开局时若抢先注册, 会把全场职业覆盖成占位船员)。
    ///   守卫只处理"开局之后才出现的人" = 中途生成(重连者)。
    /// </summary>
    private static System.Collections.Generic.HashSet<int>? _baseline;

    /// <summary>守卫扫描节流。</summary>
    private static float _scanAt;

    /// <summary>注册失败后的退避截止时刻。</summary>
    private static float _retryAt;

    /// <summary>已经强制幽灵化过的玩家(避免每轮重复调 Die)。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _forcedGhost =
        new System.Collections.Generic.HashSet<int>();

    /// <summary>该玩家是否是"开局之后才出现的人"(= 重连者的新身体)。</summary>
    private static bool IsMidRoundSpawn(byte pid)
        => _baseline != null && !_baseline.Contains(pid);

    internal static void Reset()
    {
        _baseline = null;
        _scanAt = 0f;
        _retryAt = 0f;
        _forcedGhost.Clear();
    }

    /// <summary>死亡态镜像的节流时刻。</summary>
    private static float _deathMirrorAt;

    /// <summary>
    ///     把"香草层已死亡"镜像到本机的 NoS 层。
    ///
    ///   为什么需要: NOS 认为某玩家是死是活, 看的是它自己的 PlayerModInfo.IsDead,
    ///   和香草的 Data.IsDead 是【两套独立记账】。重连者的死亡态是房主/本人写进
    ///   香草层的, 其他客户端拿到了这个数据, 但它们的 NoS 层并不知情 ——
    ///   于是本机会把重连者继续画成活人, 还能被杀、会议里也不显示死亡。
    ///
    ///   这里每 0.5 秒对一次账: 香草已死但本机 NoS 还活着 → 本地灵魂化(两层一起写)。
    ///   正常死亡时 NOS 自己会把两层都写好, 不会出现这种不一致, 因此不会被误触发。
    /// </summary>
    private static void MirrorVanillaDeathToNoS()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < _deathMirrorAt)
        {
            return;
        }

        _deathMirrorAt = now + 0.5f;

        var pcs = PlayerControl.AllPlayerControls;
        if (pcs == null)
        {
            return;
        }

        for (var i = 0; i < pcs.Count; i++)
        {
            var pc = pcs[i];
            if (pc == null || pc.Data == null)
            {
                continue;
            }

            bool vanillaDead;
            try { vanillaDead = pc.Data.IsDead; } catch { continue; }

            if (!vanillaDead)
            {
                continue;
            }

            var nosP = Virial.Game.Player.GetPlayer(pc.PlayerId);
            if (nosP == null)
            {
                continue;
            }

            // ★ 中途生成的人(重连者的新身体): 即使香草数据已经是"死亡",
            //   也必须强制走一次 Die() —— 否则角色不会切成幽灵外观,
            //   身体会以活人实体继续显示在场。
            try
            {
                if (IsMidRoundSpawn(pc.PlayerId) && _forcedGhost.Add(pc.PlayerId))
                {
                    var np = Virial.Game.Player.GetPlayer(pc.PlayerId);
                    NebulaStateCodec.MakeGhostLocal(pc, np, true);
                    NebulaResumeEntry.Log("身体处理: 重连者 id=" + pc.PlayerId
                        + " 已强制走幽灵化(Die), 身体不再以实体显示。");
                }
            }
            catch { }

            var nosDead = false;
            try
            {
                var prop = nosP.GetType().GetProperty("IsDead",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(nosP);
                    nosDead = v is bool b && b;
                }
            }
            catch { }

            if (nosDead)
            {
                continue;
            }

            try
            {
                NebulaStateCodec.MakeGhostLocal(pc, nosP);
                NebulaResumeEntry.Log("死亡镜像: 香草层已死亡但本机 NoS 仍为活人 → 已本地灵魂化 id="
                    + pc.PlayerId + "。");
            }
            catch { }
        }
    }

    /// <summary>
    ///     本机是不是"对局开始之后才出现的人"(= 重连者 / 中途加入者)。
    ///     用来判断"要不要在没有续局信号的情况下也把环境搭起来"。
    /// </summary>
    internal static bool IsMidRoundLocal()
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || _baseline == null)
            {
                return false;
            }

            return !_baseline.Contains(lp.PlayerId);
        }
        catch
        {
            return false;
        }
    }

    private static float _fastFillAt;

    /// <summary>
    ///     快速补职业: 中途出现的玩家已被 NOS 自己注册、但职业还是空的那一两秒里,
    ///     NOS 的击杀判定(OnCheckCanKill → IsImpostor)等每帧都会空引用。
    ///     完整扫描(Tick)是 1 秒一次, 这里每 0.2 秒只做"职业为空就补占位"这一件事,
    ///     把这个窗口压到最短。开局就在场的玩家一律不碰。
    /// </summary>
    internal static void FastFillRoles()
    {
        try
        {
            if (_baseline == null || ShipStatus.Instance == null)
            {
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            if (now < _fastFillAt)
            {
                return;
            }

            _fastFillAt = now + 0.2f;

            var pcs = PlayerControl.AllPlayerControls;
            if (pcs == null)
            {
                return;
            }

            object? crew = null;

            for (var i = 0; i < pcs.Count; i++)
            {
                var pc = pcs[i];
                if (pc == null || pc.Data == null || _baseline.Contains(pc.PlayerId))
                {
                    continue;
                }

                var nosP = Virial.Game.Player.GetPlayer(pc.PlayerId);
                if (nosP == null || nosP.Role != null)
                {
                    continue;
                }

                crew ??= ResumeTransition.CrewRole();
                if (crew == null)
                {
                    return;
                }

                ResumeTransition.SetRoleLocal(nosP, crew);
                FrameGuard.Arm();
                NebulaResumeEntry.Log("生成守卫(快速): 玩家 id=" + pc.PlayerId + " 职业为空, 已补占位职业。");
            }
        }
        catch { }
    }

    /// <summary>由 Entry.Tick 每帧调用。</summary>
    internal static void Tick()
    {
        try
        {
            // ① 死亡态镜像(所有玩家, 幂等; 正常死亡时两层一致 → 不动作)
            MirrorVanillaDeathToNoS();

            if (ShipStatus.Instance == null)
            {
                // 离开对局: 清空本轮基准, 让下一局重新采样
                if (_baseline != null)
                {
                    _baseline = null;
                    _forcedGhost.Clear();
                    FrameGuard.Reset();
                    DeadRoster.Reset();
                    SpectatorSeat.Reset();
                    MeetingSeat.Reset();
                    MeetingDefer.Reset();
                    MeetingProxy.Reset();
                }

                return;
            }

            var pcs = PlayerControl.AllPlayerControls;
            if (pcs == null || pcs.Count == 0)
            {
                return;
            }

            // ② 本轮开局基准: 第一次进对局时, 把在场的人全部记录为"不干预"
            if (_baseline == null)
            {
                var baseSet = new System.Collections.Generic.HashSet<int>();
                for (var i = 0; i < pcs.Count; i++)
                {
                    var pc0 = pcs[i];
                    if (pc0 != null)
                    {
                        try { baseSet.Add(pc0.PlayerId); } catch { }
                    }
                }

                _baseline = baseSet;
                NebulaResumeEntry.Log("生成守卫: 本轮基准 " + baseSet.Count
                    + " 人(开局玩家不干预, 只处理中途出现的玩家)。");
                return;
            }

            // ③ 只处理"开局之后才出现的人" = 中途生成(重连者)
            var now = UnityEngine.Time.unscaledTime;
            if (now < _scanAt)
            {
                return;
            }

            _scanAt = now + 1f;

            if (now < _retryAt)
            {
                return;
            }

            var ngmType = ResumeTransition.Find("Nebula.Game.NebulaGameManager");
            if (ngmType == null)
            {
                return;
            }

            var ngm = ngmType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

            if (ngm == null)
            {
                _retryAt = now + 0.25f;
                return;
            }

            var register = ngmType.GetMethod("RegisterPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var getPlayer = ngmType.GetMethod("GetPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(byte) }, null);

            if (register == null)
            {
                return;
            }

            var crew = ResumeTransition.CrewRole();

            for (var i = 0; i < pcs.Count; i++)
            {
                var pc = pcs[i];
                if (pc == null || pc.Data == null)
                {
                    continue;
                }

                byte pid;
                try { pid = pc.PlayerId; } catch { continue; }

                // ★ 开局就在场 → 绝不干预(NOS 负责他们的真实职业)
                if (_baseline.Contains(pid))
                {
                    continue;
                }

                // 观察到中途出现的玩家 → 本机立即进入帧保护期
                // (即使 NOS 已自行注册, 半初始化窗口仍可能每帧抛空引用)
                FrameGuard.Arm();

                var nosP = Virial.Game.Player.GetPlayer(pid);

                if (nosP == null)
                {
                    object? confirmed = null;
                    try { confirmed = getPlayer?.Invoke(ngm, new object[] { pid }); } catch { }
                    if (confirmed == null)
                    {
                        try
                        {
                            var created = register.Invoke(ngm, new object[] { pc });
                            if (created != null && crew != null)
                            {
                                try { ResumeTransition.SetRoleLocal(created, crew); } catch { }
                            }

                            FrameGuard.Arm();   // 中途生成 → 本机也进入帧保护期
                            NebulaResumeEntry.Log("生成守卫: 中途出现的玩家 id=" + pid
                                + " 已注册进本机 NoS 并装占位职业。");
                        }
                        catch (System.Exception re)
                        {
                            _retryAt = now + 0.25f;
                            NebulaResumeEntry.Log("生成守卫: 注册 id=" + pid
                                + " 暂未成功, 250ms 后重试: " + re.Message);
                            break;
                        }

                        continue;
                    }

                    nosP = Virial.Game.Player.GetPlayer(pid);
                }

                if (nosP == null)
                {
                    continue;
                }

                // 已注册但职业为空 → 补占位职业(否则 IsImpostor/CalcSize 每帧空引用)
                try
                {
                    if (nosP.Role == null)
                    {
                        if (crew != null)
                        {
                            ResumeTransition.SetRoleLocal(nosP, crew);
                        }

                        FrameGuard.Arm();
                        NebulaResumeEntry.Log("生成守卫: 玩家 id=" + pid
                            + " 已有 NoS 对象但无职业, 已补占位职业。");
                    }
                }
                catch { }
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("生成守卫异常: " + e.Message);
        }
    }
}
