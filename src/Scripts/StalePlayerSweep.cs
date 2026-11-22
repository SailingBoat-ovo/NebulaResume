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
        _missingSince.Clear();
        _winSince = -1f;
        _staleSince.Clear();
        _purged.Clear();
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

            _missingSince.Clear();

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

            if (HasLiveBody(p) && !GoneFromGameData(p))
            {
                continue;
            }

            MarkDisconnected(p);
        }

        HostWinDiagnostic();
        HostSettlementPurge();
        HostWinWatchdog();
    }

    // ── 房主: 结算前删掉失效身份 ──
    //   NoS 不会删除玩家: 有人离场只标"已断开", 他的职业/附加职业的逻辑仍然挂着。
    //   重连者每回来一次就多一个旧身份, 离场没回来的人也留着身份 ——
    //   这些身份的职业逻辑可能继续参与胜负判定(例如声称"本阵营还有人在"), 让对局结算不了。
    //   规则: 当胜负条件看起来已经达成(只数仍在房间里的身份) → 把失效身份
    //     · 离场且没回来的身份;
    //     · 已经以新身份重连回来的旧身份(旧编号已不在房间资料里)
    //   的职业、幽灵职业、附加职业全部停用, 并从 NoS 的玩家表里删除, 然后交给 NoS 自己判定。
    //   只在房主、对局进行中、会议/放逐之外做; 失效需持续 2 秒(避免重连途中的瞬间状态)。
    private static readonly Dictionary<byte, float> _staleSince = new();
    private static readonly HashSet<byte> _purged = new();
    private static float _purgeAt;

    private static void HostSettlementPurge()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < _purgeAt)
        {
            return;
        }

        _purgeAt = now + 0.5f;

        try
        {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost
                || MeetingHud.Instance != null || ExileController.Instance != null || GameData.Instance == null)
            {
                _staleSince.Clear();
                return;
            }

            var ngm = _ngmInstance?.GetValue(null);
            if (ngm == null)
            {
                return;
            }

            var stale = new List<Player>();
            int alive = 0, imp = 0;
            var seen = new HashSet<byte>();
            foreach (var p in Player.AllPlayers)
            {
                if (p == null)
                {
                    continue;
                }

                byte pid;
                try { pid = p.PlayerId; } catch { continue; }
                seen.Add(pid);

                if (IsStale(p, pid))
                {
                    if (!_staleSince.TryGetValue(pid, out var since))
                    {
                        _staleSince[pid] = now;
                    }
                    else if (now - since >= 2f)
                    {
                        stale.Add(p);
                    }

                    continue;
                }

                _staleSince.Remove(pid);

                bool dead = true, isImp = false;
                try { dead = p.IsDead; } catch { }
                try { isImp = p.IsImpostor; } catch { }
                if (!dead)
                {
                    alive++;
                    if (isImp)
                    {
                        imp++;
                    }
                }
            }

            foreach (var k in new List<byte>(_staleSince.Keys))
            {
                if (!seen.Contains(k))
                {
                    _staleSince.Remove(k);
                }
            }

            if (stale.Count == 0 || alive == 0)
            {
                return;
            }

            // 胜负条件看起来达成: 伪装者击杀胜利(伪装者 ×2 ≥ 存活), 或伪装者全灭。
            var settle = imp * 2 >= alive || imp == 0;
            if (!settle)
            {
                return;
            }

            // 记下"重连留下的旧身份"对应的名字(还有同名身份在房间里 = 这个人已经以新身份回来了),
            //   结算总结里给他标「重连玩家」。
            var presentNames = new HashSet<string>();
            foreach (var p in Player.AllPlayers)
            {
                try
                {
                    if (p != null && !p.IsDisconnected)
                    {
                        presentNames.Add(p.Name);
                    }
                }
                catch { }
            }

            foreach (var p in stale)
            {
                try
                {
                    if (presentNames.Contains(p.Name))
                    {
                        EndSummary.NoteRejoin(p.Name);
                    }
                }
                catch { }
            }

            var removed = new List<string>();
            foreach (var p in stale)
            {
                if (Purge(ngm, p))
                {
                    removed.Add(p.PlayerId.ToString());
                }
            }

            if (removed.Count > 0)
            {
                try
                {
                    var gomType = ResumeTransition.Find("Nebula.Game.GameOperatorManager");
                    var gom = gomType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    gomType?.GetMethod("WrapUpDeadLifespans", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(gom, null);
                }
                catch { }

                NebulaResumeEntry.Log("结算清理(房主): 胜负条件已达成(仍在房间 存活 " + alive + " 人, 伪装者 " + imp
                    + " 人), 删除失效身份 [" + string.Join(",", removed) + "](离场未回 / 已重连的旧身份): 停用其职业与附加职业并移出 NoS 玩家表, 交给 NoS 判定结算。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("结算清理(房主) 出错: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>失效身份: NoS 标为已断开, 且房间资料里已经没有这个编号(或也标为断开)。</summary>
    private static bool IsStale(Player p, byte pid)
    {
        try
        {
            if (!p.IsDisconnected)
            {
                return false;
            }

            var info = GameData.Instance.GetPlayerById(pid);
            return info == null || info.Disconnected;
        }
        catch
        {
            return false;
        }
    }

    private static bool Purge(object ngm, Player p)
    {
        byte pid;
        try { pid = p.PlayerId; } catch { return false; }

        // ① 停用职业 / 幽灵职业 / 附加职业(结束它们挂在 NoS 上的全部逻辑)
        try { Inactivate(p.Role); } catch { }
        try { Inactivate(p.GetType().GetProperty("GhostRole")?.GetValue(p)); } catch { }
        try
        {
            foreach (var m in p.Modifiers)
            {
                Inactivate(m);
            }
        }
        catch { }

        // ② 移出 NoS 玩家表
        var ok = false;
        try
        {
            var t = ngm.GetType();
            if (t.GetField("allModPlayers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(ngm) is System.Collections.IDictionary mods
                && mods.Contains(pid))
            {
                mods.Remove(pid);
                ok = true;
            }

            if (t.GetField("allPlayerlikes", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(ngm) is System.Collections.IDictionary likes)
            {
                var key = (int)pid;
                if (likes.Contains(key))
                {
                    likes.Remove(key);
                }
            }

            t.GetField("allOrderedPlayers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(ngm, null);
        }
        catch { }

        if (ok)
        {
            _purged.Add(pid);
        }

        return ok;
    }

    private static void Inactivate(object? assignable)
    {
        if (assignable == null)
        {
            return;
        }

        try
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo? m = null;
            for (var t = assignable.GetType(); t != null && m == null; t = t.BaseType)
            {
                m = t.GetMethod("Inactivate", F | BindingFlags.DeclaredOnly, null, System.Type.EmptyTypes, null);
            }

            if (m == null)
            {
                foreach (var itf in assignable.GetType().GetInterfaces())
                {
                    m = itf.GetMethod("Inactivate", System.Type.EmptyTypes);
                    if (m != null)
                    {
                        break;
                    }
                }
            }

            m?.Invoke(assignable, null);
        }
        catch { }
    }

    // ── 房主: 应当结算却迟迟不结算 ──
    //   NoS 的胜负检查在房主每帧跑, 满足条件就立刻结算。若按同样口径已满足 3 秒仍没结算,
    //   说明检查被卡住了。已知可能:
    //     · NoS 的"暂缓胜负检查"段(WinCheckBlocker)在一次带异常的批量 RPC 后没关上 → 之后永远不检查;
    //     · 胜负检查本身没跑到。
    //   做法: 关掉残留的暂缓段, 再走 NoS 自己的结算入口(TriggerGameEnd + CheckAndTriggerGameEnd),
    //   NoS 的阻止条件(别的击杀阵营在场 / 职业阻止)照常生效, 我们不越过它们, 只记日志。
    private static float _winSince = -1f;
    private static float _watchAt;
    private static float _watchRetryAt;

    private static void HostWinWatchdog()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < _watchAt)
        {
            return;
        }

        _watchAt = now + 0.5f;

        try
        {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost
                || MeetingHud.Instance != null || ExileController.Instance != null)
            {
                _winSince = -1f;
                return;
            }

            _ngmType ??= ResumeTransition.Find("Nebula.Game.NebulaGameManager");
            var ngm = _ngmInstance?.GetValue(null);
            if (ngm == null || _ngmType == null)
            {
                return;
            }

            if (_ngmType.GetProperty("EndState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ngm) != null)
            {
                _winSince = -1f;
                return;
            }

            int alive = 0, imp = 0;
            var lover = false;
            var roster = new System.Text.StringBuilder();
            foreach (var p in Player.AllPlayers)
            {
                if (p == null)
                {
                    continue;
                }

                bool dead;
                try { dead = p.IsDead; } catch { continue; }
                if (dead)
                {
                    continue;
                }

                alive++;
                bool isImp = false;
                try { isImp = p.IsImpostor; } catch { }
                if (isImp)
                {
                    imp++;
                }

                try
                {
                    foreach (var m in p.Modifiers)
                    {
                        if (m?.Modifier != null && m.Modifier.GetType().FullName?.Contains("Lover") == true)
                        {
                            lover = true;
                        }
                    }
                }
                catch { }

                roster.Append(" [").Append(p.PlayerId).Append(' ').Append(RoleName(p)).Append(isImp ? " 伪" : "").Append(']');
            }

            if (imp == 0 || imp * 2 < alive || lover)
            {
                _winSince = -1f;
                return;
            }

            if (_winSince < 0f)
            {
                _winSince = now;
                return;
            }

            if (now - _winSince < 3f || now < _watchRetryAt)
            {
                return;
            }

            _watchRetryAt = now + 5f;

            var notes = new List<string>();

            // ① 残留的暂缓胜负检查段
            var blocker = ResumeTransition.Find("Nebula.Game.WinCheckBlocker");
            var secField = blocker?.GetField("currentSection", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var sec = secField?.GetValue(null);
            if (sec != null)
            {
                var label = sec.GetType().GetField("Name")?.GetValue(sec) as string;
                secField!.SetValue(null, null);
                notes.Add("关闭了残留的暂缓胜负检查段「" + label + "」");
            }

            // ② 别的击杀阵营是否仍在场(NoS 规则, 不越过)
            var remaining = OtherKillerTeamRemaining();
            if (remaining == true)
            {
                NebulaResumeEntry.Log("结算保护(房主): 存活 " + alive + " 人/伪装者 " + imp + " 人已满足人数, 但 NoS 报告还有别的击杀阵营在场 → 按 NoS 规则不结算。存活:" + roster
                    + (notes.Count > 0 ? " | " + string.Join("; ", notes) : ""));
                return;
            }

            // ③ 走 NoS 自己的结算入口
            var triggered = TriggerImpostorKillWin(ngm);
            notes.Add(triggered ? "已按 NoS 规则提交伪装者击杀胜利" : "提交结算失败");

            NebulaResumeEntry.Log("结算保护(房主): 存活 " + alive + " 人/伪装者 " + imp + " 人, 满足伪装者击杀胜利已超过 3 秒仍未结算 → "
                + string.Join("; ", notes) + "。存活:" + roster);
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("结算保护(房主) 出错: " + e.GetType().Name + " : " + e.Message);
        }
    }

    private static string RoleName(Player p)
    {
        try
        {
            var role = p.Role?.Role;
            if (role == null)
            {
                return "?";
            }

            var n = role.GetType().GetProperty("InternalName")?.GetValue(role) as string;
            return string.IsNullOrEmpty(n) ? role.GetType().Name : n!;
        }
        catch
        {
            return "?";
        }
    }

    private static System.Type? FindSimple(string simpleName)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = asm.GetName().Name ?? "";
            if (!n.StartsWith("Nebula") && !n.StartsWith("Virial"))
            {
                continue;
            }

            System.Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t != null && t.Name == simpleName)
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static System.Type? _killerCbType;
    private static object? _impTeam;

    /// <summary>按 NoS 的方式问一遍"伪装者以外的击杀阵营是否还在场"。null = 问不了。</summary>
    private static bool? OtherKillerTeamRemaining()
    {
        try
        {
            _killerCbType ??= FindSimple("KillerTeamCallback");
            if (_impTeam == null)
            {
                var teams = FindSimple("NebulaTeams");
                _impTeam = teams?.GetField("ImpostorTeam", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
                           ?? teams?.GetProperty("ImpostorTeam", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            }

            var gomType = ResumeTransition.Find("Nebula.Game.GameOperatorManager");
            var gom = gomType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (_killerCbType == null || _impTeam == null || gom == null)
            {
                return null;
            }

            var cb = System.Activator.CreateInstance(_killerCbType, _impTeam);
            MethodInfo? run = null;
            foreach (var m in gomType!.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.Name == "Run" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3)
                {
                    run = m;
                    break;
                }
            }

            if (run == null)
            {
                return null;
            }

            run.MakeGenericMethod(_killerCbType).Invoke(gom, new object[] { cb!, false, true });
            return _killerCbType.GetProperty("RemainingOtherTeam")?.GetValue(cb) as bool?;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Game.TriggerGameEnd(ImpostorWin, 击杀, null) 后立刻 CriteriaManager.CheckAndTriggerGameEnd()。</summary>
    private static bool TriggerImpostorKillWin(object ngm)
    {
        try
        {
            var endType = ResumeTransition.Find("Nebula.Game.NebulaGameEnd");
            var impWin = endType?.GetField("ImpostorWin", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (impWin == null)
            {
                return false;
            }

            MethodInfo? trigger = null;
            foreach (var itf in ngm.GetType().GetInterfaces())
            {
                trigger = itf.GetMethod("TriggerGameEnd");
                if (trigger != null)
                {
                    break;
                }
            }

            if (trigger == null)
            {
                return false;
            }

            var ps = trigger.GetParameters();
            trigger.Invoke(ngm, new[] { impWin, System.Enum.ToObject(ps[1].ParameterType, 1), null });

            var cm = ngm.GetType().GetProperty("CriteriaManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ngm);
            cm?.GetType().GetMethod("CheckAndTriggerGameEnd", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(cm, null);
            return true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("结算保护(房主): 提交结算出错: " + (e.InnerException ?? e).Message);
            return false;
        }
    }

    // ── 已离开名单的旧身份 ──
    //   重连者以新身份回来后, 旧身份会从 GameData 里移除。正常情况下 NoS 会在 HandleDisconnect
    //   里把它标成已断线; 但如果旧身份的身体残留, 或者中途被别的逻辑改回"在场",
    //   NoS 就把这个已经不存在的人一直算作活人, 胜负判定的存活人数永远多一个 → 不结算。
    //   GameData 里没有(或标为已断开)且持续 2 秒以上 → 按掉线处理。会议/放逐期间不动(代坐需要旧座位)。
    private static readonly Dictionary<byte, float> _missingSince = new();

    private static bool GoneFromGameData(Player p)
    {
        byte pid;
        try { pid = p.PlayerId; } catch { return false; }

        if (MeetingHud.Instance != null || ExileController.Instance != null || GameData.Instance == null)
        {
            _missingSince.Remove(pid);
            return false;
        }

        var gone = false;
        try
        {
            var info = GameData.Instance.GetPlayerById(pid);
            gone = info == null || info.Disconnected;
        }
        catch { return false; }

        if (!gone)
        {
            _missingSince.Remove(pid);
            return false;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (!_missingSince.TryGetValue(pid, out var since))
        {
            _missingSince[pid] = now;
            return false;
        }

        return now - since >= 2f;
    }

    // ── 房主诊断: NoS 的胜负判定看到的人数 ──
    //   NoS 的击杀胜利: 存活伪装者 ×2 ≥ 存活人数。这里每 5 秒按同样的口径数一遍,
    //   有变化才打一行; 如果"应当结算却没结算", 一眼就能看出是哪个人被多算了。
    private static float _diagAt;
    private static string _lastDiag = "";

    private static void HostWinDiagnostic()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < _diagAt)
        {
            return;
        }

        _diagAt = now + 5f;

        try
        {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            int alive = 0, imp = 0, total = 0, present = 0, gone = 0, spect = 0;
            foreach (var p in Player.AllPlayers)
            {
                if (p == null)
                {
                    continue;
                }

                bool dead = true, disc = false, isImp = false;
                try { dead = p.IsDead; } catch { }
                try { disc = p.IsDisconnected; } catch { }
                try { isImp = p.IsImpostor; } catch { }

                var inData = false;
                try { inData = GameData.Instance != null && GameData.Instance.GetPlayerById(p.PlayerId) != null; } catch { }

                if (!dead)
                {
                    alive++;
                    if (isImp)
                    {
                        imp++;
                    }
                }

                total++;
                var isSpect = RoleName(p).Contains("Spectator") || RoleName(p).Contains("spectator");
                if (disc)
                {
                    gone++;
                }
                else
                {
                    present++;
                    if (isSpect)
                    {
                        spect++;
                    }
                }

                sb.Append(" [").Append(p.PlayerId).Append(dead ? " 死" : " 活").Append(disc ? " 断" : "").Append(isSpect ? " 观" : "")
                  .Append(isImp ? " 伪" : "").Append(inData ? "" : " 无资料").Append(']');
            }

            var line = "身份 " + total + " 个(在场 " + present + ", 其中观战 " + spect + "; 已离场/重连留下的旧身份 " + gone + ") | "
                + "存活 " + alive + " 人, 其中伪装者 " + imp + " 人 →" + (imp * 2 >= alive && alive > 0 ? " 已满足伪装者击杀胜利" : " 未满足") + " ::" + sb;
            if (line != _lastDiag)
            {
                _lastDiag = line;
                NebulaResumeEntry.Log("胜负诊断(房主): " + line);
            }
        }
        catch { }
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
