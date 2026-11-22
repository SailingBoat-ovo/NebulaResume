// ============================================================
// EndSummary.cs — 结算画面的玩家总结
//
// ▸ 解决什么
//   NoS 的结算总结按"玩家表里的每个身份"各列一行, 职业链取自职业历史。
//   重连者每回来一次就多一个身份(旧身份标为掉线), 观战者卡退重进也一样 ——
//   同一个人会出现好几行; 我们为了让 NoS 对象完整而装的"占位船员"也会进职业链,
//   显示成"船 → 正义的换票师 → …"、"船 → 观战中"。
//
// ▸ 怎么做(只在生成总结的那一刻临时调整, 生成完立即还原, 不影响对局记录/统计)
//   · 同名的多个身份合并成一行: 保留仍在房间里的那个; 都已离开就保留最新(编号最大)的。
//     两个及以上同名的人都还在房间里 → 认为是不同的人, 不合并。
//   · 职业链里去掉占位船员(仅当这个身份还有别的真实职业时; 否则保留, 避免整行空掉)。
//   · 重连回来的人(合并过, 或房主结算清理时确认是重连留下的旧身份), 状态后面标「(重连玩家)」。
//     观战者不标(他们是中途加入的新玩家)。
// ============================================================

using System.Reflection;
using HarmonyLib;

namespace NebulaResume;

internal static class EndSummary
{
    private sealed class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    /// <summary>我们装上去的占位船员职业实例。</summary>
    private static readonly HashSet<object> _placeholders = new(new RefEq());

    /// <summary>确认是重连回来的人的名字(房主结算清理时记下)。</summary>
    private static readonly HashSet<string> _rejoinNames = new();

    /// <summary>总结生成期间需要标「重连玩家」的编号。</summary>
    private static readonly HashSet<byte> _tagIds = new();

    private static bool _inSummary;
    private static List<(object Key, object Value)>? _removedPlayers;
    private static List<object>? _savedHistory;

    internal static void NotePlaceholder(object? role)
    {
        SyncGame();
        if (role != null)
        {
            _placeholders.Add(role);
        }
    }

    internal static void NoteRejoin(string? name)
    {
        SyncGame();
        if (!string.IsNullOrEmpty(name))
        {
            _rejoinNames.Add(name!);
        }
    }

    /// <summary>NoS 换了一局(新的对局对象) → 清空上一局的记录。</summary>
    private static object? _game;

    private static void SyncGame()
    {
        try
        {
            var g = Ngm();
            if (g != null && !ReferenceEquals(g, _game))
            {
                _game = g;
                _placeholders.Clear();
                _rejoinNames.Clear();
            }
        }
        catch { }
    }

    internal static void Install(Harmony harmony)
    {
        try
        {
            var t = ResumeTransition.Find("Nebula.Game.EndGameManagerSetUpPatch");
            var m = t?.GetMethod("GetRoleContent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null)
            {
                NebulaResumeEntry.Log("结算总结: 找不到 GetRoleContent, 不调整总结。");
                return;
            }

            harmony.Patch(m,
                prefix: new HarmonyMethod(typeof(EndSummary).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(EndSummary).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));

            var pmi = ResumeTransition.Find("Nebula.Player.PlayerModInfo");
            var st = pmi?.GetMethod("GetStateText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (st != null)
            {
                harmony.Patch(st, postfix: new HarmonyMethod(typeof(EndSummary).GetMethod(nameof(StatePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            }

            NebulaResumeEntry.Log("结算总结: 已挂(同一人合并为一行 / 去掉占位船员 / 标注重连玩家)。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("结算总结: 挂载失败: " + e.Message);
        }
    }

    private static object? Ngm()
    {
        var t = ResumeTransition.Find("Nebula.Game.NebulaGameManager");
        return t?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
    }

    private static string NameOf(object p)
    {
        foreach (var n in new[] { "Name", "PlayerName" })
        {
            try
            {
                var prop = p.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop?.GetValue(p) is string s && s.Length > 0)
                {
                    return s;
                }

                foreach (var itf in p.GetType().GetInterfaces())
                {
                    prop = itf.GetProperty(n);
                    if (prop?.GetValue(p) is string s2 && s2.Length > 0)
                    {
                        return s2;
                    }
                }
            }
            catch { }
        }

        return "";
    }

    private static bool IsSpectator(Virial.Game.Player p)
    {
        try
        {
            var role = p.Role?.Role;
            return role != null && role.GetType().Name.Contains("Spectator");
        }
        catch
        {
            return false;
        }
    }

    private static object? Get(object o, string name)
    {
        var t = o.GetType();
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return t.GetProperty(name, F)?.GetValue(o) ?? t.GetField(name, F)?.GetValue(o);
    }

    private static void Prefix()
    {
        _inSummary = true;
        _tagIds.Clear();
        _removedPlayers = null;
        _savedHistory = null;

        try
        {
            var ngm = Ngm();
            if (ngm == null)
            {
                return;
            }

            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var mods = ngm.GetType().GetField("allModPlayers", F)?.GetValue(ngm) as System.Collections.IDictionary;
            if (mods == null)
            {
                return;
            }

            // ── ① 同名身份合并 ──
            var groups = new Dictionary<string, List<Virial.Game.Player>>();
            foreach (var v in mods.Values)
            {
                if (v is not Virial.Game.Player p)
                {
                    continue;
                }

                var name = NameOf(p);
                if (name.Length == 0)
                {
                    continue;
                }

                if (!groups.TryGetValue(name, out var list))
                {
                    groups[name] = list = new List<Virial.Game.Player>();
                }

                list.Add(p);
            }

            var removed = new List<(object, object)>();
            var notes = new List<string>();
            foreach (var kv in groups)
            {
                var list = kv.Value;
                Virial.Game.Player? keep = null;

                if (list.Count > 1)
                {
                    var present = list.FindAll(x => { try { return !x.IsDisconnected; } catch { return false; } });
                    if (present.Count >= 2)
                    {
                        continue;   // 同名的不同玩家, 不合并
                    }

                    keep = present.Count == 1 ? present[0] : list[0];
                    if (present.Count == 0)
                    {
                        foreach (var x in list)
                        {
                            if (x.PlayerId > keep.PlayerId)
                            {
                                keep = x;
                            }
                        }
                    }

                    foreach (var x in list)
                    {
                        if (ReferenceEquals(x, keep))
                        {
                            continue;
                        }

                        object key = x.PlayerId;
                        if (mods.Contains(key))
                        {
                            removed.Add((key, mods[key]!));
                            mods.Remove(key);
                        }
                    }

                    notes.Add(kv.Key + " " + list.Count + "→1");
                }
                else if (_rejoinNames.Contains(kv.Key))
                {
                    keep = list[0];
                }

                if (keep != null && !IsSpectator(keep))
                {
                    _tagIds.Add(keep.PlayerId);
                }
            }

            if (removed.Count > 0)
            {
                _removedPlayers = removed;
                ngm.GetType().GetField("allOrderedPlayers", F)?.SetValue(ngm, null);
            }

            // ── ② 职业链去掉占位船员 ──
            if (ngm.GetType().GetField("RoleHistory", F)?.GetValue(ngm) is System.Collections.IList hist && _placeholders.Count > 0)
            {
                var real = new HashSet<byte>();
                foreach (var h in hist)
                {
                    if (h == null || Get(h, "IsModifier") is true)
                    {
                        continue;
                    }

                    var a = Get(h, "Assignable");
                    if (a is Virial.Assignable.RuntimeRole && a != null && !_placeholders.Contains(a) && Get(h, "PlayerId") is byte pid)
                    {
                        real.Add(pid);
                    }
                }

                var saved = new List<object>();
                foreach (var h in hist)
                {
                    saved.Add(h!);
                }

                var cut = 0;
                for (var i = hist.Count - 1; i >= 0; i--)
                {
                    var h = hist[i];
                    if (h == null || Get(h, "IsModifier") is true)
                    {
                        continue;
                    }

                    var a = Get(h, "Assignable");
                    if (a != null && _placeholders.Contains(a) && Get(h, "PlayerId") is byte pid && real.Contains(pid))
                    {
                        hist.RemoveAt(i);
                        cut++;
                    }
                }

                if (cut > 0)
                {
                    _savedHistory = saved;
                    notes.Add("占位船员 " + cut + " 条");
                }
            }

            if (notes.Count > 0 || _tagIds.Count > 0)
            {
                NebulaResumeEntry.Log("结算总结: 合并/隐藏 [" + string.Join(", ", notes) + "], 标注重连玩家 " + _tagIds.Count + " 人。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("结算总结: 调整失败(按原样显示): " + e.Message);
        }
    }

    private static System.Exception? Finalizer(System.Exception? __exception)
    {
        _inSummary = false;

        try
        {
            var ngm = Ngm();
            if (ngm != null)
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                if (_removedPlayers != null
                    && ngm.GetType().GetField("allModPlayers", F)?.GetValue(ngm) is System.Collections.IDictionary mods)
                {
                    foreach (var (k, v) in _removedPlayers)
                    {
                        if (!mods.Contains(k))
                        {
                            mods.Add(k, v);
                        }
                    }

                    ngm.GetType().GetField("allOrderedPlayers", F)?.SetValue(ngm, null);
                }

                if (_savedHistory != null
                    && ngm.GetType().GetField("RoleHistory", F)?.GetValue(ngm) is System.Collections.IList hist)
                {
                    hist.Clear();
                    foreach (var h in _savedHistory)
                    {
                        hist.Add(h);
                    }
                }
            }
        }
        catch { }

        _removedPlayers = null;
        _savedHistory = null;

        if (__exception != null)
        {
            NebulaResumeEntry.Log("结算总结: 生成时出错: " + __exception.Message);
        }

        return __exception;
    }

    private static void StatePostfix(object __instance, ref string __result)
    {
        if (!_inSummary)
        {
            return;
        }

        try
        {
            if (__instance is Virial.Game.Player p && _tagIds.Contains(p.PlayerId))
            {
                __result += " <color=#7FD4FF>(重连玩家)</color>";
            }
        }
        catch { }
    }
}
