// ============================================================
// CosmeticsWarmup.cs — 中途加入后补齐装扮缓存
//
// ▸ 现象
//   中途加入(重连 / 观战)的玩家, 之后的会议里投票格只剩名字:
//   自己和其他人的帽子、皮肤、名牌底图全都不显示(投票本身正常)。
//   日志里对应的是会议开始时的
//       CosmeticsCache.GetNameplate  NullReferenceException
//   PlayerVoteArea.SetCosmetics 在第一步取名牌就抛了异常, 后面的装扮一项也没设上。
//
// ▸ 原因
//   装扮的显示资源存在【船上的装扮缓存】(ShipStatus.CosmeticsCache)里,
//   只有开局介绍动画那一步会把全场玩家的装扮一次性载入(PopulateFromPlayers)。
//   中途加入的人没有经历开局介绍, 他本机的缓存是空的;
//   同理, 正常开局的玩家本机也没有"中途加入者"的那一份。
//
// ▸ 做法
//   · 中途加入者: 过渡完成后, 立刻为全场载入一次(相当于补上开局那一步)。
//   · 所有客户端: 对局中全场装扮组合发生变化(有人中途加入 / 换了装扮数据)时,
//     再载入一次。PopulateFromPlayers 只补缺失的条目, 已有的不会重复载入。
//   正常开局的玩家在第一次观察时只记录现状, 不额外动作 —— 开局介绍已经载入过了。
// ============================================================

namespace NebulaResume;

internal static class CosmeticsWarmup
{
    /// <summary>上一次已载入的全场装扮组合(签名); null = 本局还没观察过。</summary>
    private static string? _loadedSignature;

    /// <summary>过渡完成后要求立刻载入一次(中途加入者)。</summary>
    private static bool _forceNext;

    private static float _checkAt;

    /// <summary>检查间隔(秒)。</summary>
    private const float CheckInterval = 1f;

    /// <summary>由续局过渡在完成时调用: 本机是中途加入者, 缓存是空的。</summary>
    internal static void OnEnteredMidRound()
    {
        _forceNext = true;
        _checkAt = 0f;
    }

    /// <summary>由 Entry.Tick 调用。</summary>
    internal static void Tick()
    {
        var ship = ShipStatus.Instance;
        if (ship == null)
        {
            _loadedSignature = null;
            _forceNext = false;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _checkAt)
        {
            return;
        }

        _checkAt = now + CheckInterval;

        var signature = BuildSignature();
        if (signature == null)
        {
            return;   // 资料还没同步过来, 下一轮再看
        }

        if (_loadedSignature == null && !_forceNext)
        {
            // 正常开局: 开局介绍已经载入过, 只记录现状。
            _loadedSignature = signature;
            return;
        }

        if (!_forceNext && signature == _loadedSignature)
        {
            return;
        }

        try
        {
            var cache = ship.CosmeticsCache;
            if (cache == null)
            {
                return;
            }

            ship.StartCoroutine(cache.PopulateFromPlayers());
            NebulaResumeEntry.Log("装扮缓存: 已为全场载入装扮资源("
                + (_forceNext ? "中途加入后补齐" : "全场装扮有变化") + ")。");

            _loadedSignature = signature;
            _forceNext = false;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("装扮缓存: 载入失败: " + e.GetType().Name + " : " + e.Message);
            _checkAt = now + 5f;
        }
    }

    /// <summary>
    ///     全场装扮组合的签名: 每个玩家的 帽子/面饰/皮肤/宠物/名牌。
    ///     任何一个玩家的资料还没就绪就返回 null(等下一轮), 避免对着半份资料载入。
    /// </summary>
    private static string? BuildSignature()
    {
        try
        {
            var gd = GameData.Instance;
            if (gd == null)
            {
                return null;
            }

            var all = gd.AllPlayers;
            if (all == null || all.Count == 0)
            {
                return null;
            }

            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < all.Count; i++)
            {
                var info = all[i];
                if (info == null)
                {
                    continue;
                }

                var outfit = info.DefaultOutfit;
                if (outfit == null)
                {
                    return null;
                }

                sb.Append(info.PlayerId).Append(':')
                  .Append(outfit.HatId).Append('|')
                  .Append(outfit.VisorId).Append('|')
                  .Append(outfit.SkinId).Append('|')
                  .Append(outfit.PetId).Append('|')
                  .Append(outfit.NamePlateId).Append(';');
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
}
