// ============================================================
// OutfitSync.cs — 开会前把投票格需要的外观补齐
//
// ▸ 会议 UI 为什么会坏
//   观察到的日志(中途加入的玩家客户端):
//       PopulateButtons → MeetingArrangeButtonStartPatch.Postfix
//                      → MeetingModRpc.SortVotingArea
//                      → PlayerVoteArea.SetCosmetics  ⇒ NullReferenceException
//                      → MeetingHud.Start 抛出 ⇒ 投票格只搭了一半
//   表现就是会议界面残缺/花屏, 而且不会自愈。
//
//   根因是"投票格要画的那个玩家没有可用外观":
//   投票格的外观来自 PlayerInfo 上的字典
//       NetworkedPlayerInfo.Outfits[PlayerOutfitType.Default]
//   中途加入的玩家, 这条记录往往是空的; 空指针一进原生 SetCosmetics 就炸。
//
// ▸ 做法
//   ① 常驻对账: 每 0.5 秒检查全场 PlayerInfo, Outfits[Default] 缺失就补上
//      (直接写字典, 不走 SetOutfit —— 不改脏、不发包, 只影响本机视图);
//   ② 开会前预修: 给 MeetingHud.Start 挂 Prefix, 在投票格开始搭之前再补一遍。
//      这一条是关键 —— 之前的版本只在"会议已经出现之后"每 0.5 秒补,
//      而投票格是在 MeetingHud.Start 里一次搭完的, 补得太晚等于没补。
//
// ▸ 数据来源
//   PlayerInfo 自己就带着 DefaultOutfit(香草始终维护它), 用它回填最稳:
//   即使内容不完整, 至少是"非空对象", 原生代码不会再因为空指针中断。
// ============================================================

using System.Reflection;
using HarmonyLib;

namespace NebulaResume;

internal static class OutfitSync
{
    private const float Interval = 0.5f;

    private static float _nextAt;
    private static int _syncedTotal;

    internal static void Reset()
    {
        _nextAt = 0f;
        _syncedTotal = 0;
    }

    /// <summary>由 Entry.Tick 调用: 常驻对齐(不再只在会议期间跑)。</summary>
    internal static void Tick()
    {
        try
        {
            var now = UnityEngine.Time.unscaledTime;
            if (now < _nextAt)
            {
                return;
            }

            _nextAt = now + Interval;

            var fixedCount = FixAllNow();
            if (fixedCount > 0)
            {
                _syncedTotal += fixedCount;
                NebulaResumeEntry.Log("外观同步: 补齐 " + fixedCount + " 人(累计 " + _syncedTotal + ")。");
            }
        }
        catch { }
    }

    /// <summary>
    ///     把全场 PlayerInfo 的默认外观补齐。返回补齐的人数。
    ///     遍历的是 GameData 的玩家资料(而不是场上有身体的人) ——
    ///     投票格是按资料建的, 没有身体的资料同样需要外观。
    /// </summary>
    internal static int FixAllNow()
    {
        try
        {
            var gd = GameData.Instance;
            if (gd == null)
            {
                return 0;
            }

            var all = gd.AllPlayers;
            if (all == null)
            {
                return 0;
            }

            var fixedCount = 0;

            for (var i = 0; i < all.Count; i++)
            {
                var info = all[i];
                if (info == null)
                {
                    continue;
                }

                try
                {
                    if (FixOne(info))
                    {
                        fixedCount++;
                    }
                }
                catch { }
            }

            return fixedCount;
        }
        catch
        {
            return 0;
        }
    }

    private static bool FixOne(NetworkedPlayerInfo info)
    {
        var outfits = info.Outfits;
        if (outfits == null)
        {
            return false;
        }

        var hasEntry = false;

        try
        {
            hasEntry = outfits.ContainsKey(PlayerOutfitType.Default)
                       && outfits[PlayerOutfitType.Default] != null;
        }
        catch
        {
            hasEntry = false;
        }

        if (hasEntry)
        {
            return false;
        }

        // 直接写字典: 不改脏、不广播, 只影响本机这一份视图。
        outfits[PlayerOutfitType.Default] = info.DefaultOutfit;

        return true;
    }

    /// <summary>由 Entry 安装: 开会前预修外观。</summary>
    internal static void Install(Harmony harmony)
    {
        try
        {
            var m = AccessTools.Method(typeof(MeetingHud), "Start");
            if (m == null)
            {
                NebulaResumeEntry.Log("会议外观预修: 未找到 MeetingHud.Start, 跳过。");
                return;
            }

            harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(OutfitSync), nameof(MeetingStartPrefix))));

            NebulaResumeEntry.Log("会议外观预修: 已挂 MeetingHud.Start 前缀。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议外观预修: 挂载失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>MeetingHud.Start 之前跑: 保证投票格能拿到非空外观。</summary>
    private static void MeetingStartPrefix()
    {
        try
        {
            var n = FixAllNow();
            if (n > 0)
            {
                NebulaResumeEntry.Log("会议外观预修: 开会前补齐 " + n + " 人的外观(避免投票格空引用)。");
            }
        }
        catch { }
    }
}