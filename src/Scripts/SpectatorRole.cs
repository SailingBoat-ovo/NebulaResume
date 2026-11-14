// ============================================================
// SpectatorRole.cs — 中立观战职业【观战席】
//
// ▸ 什么时候用
//   全新玩家在【对局已开始】的情况下加入房间。它不是重连者(没有历史状态
//   可恢复), 所以给它一个独立的中立职业, 让它以灵魂身份旁观这一局。
//
//   与【重连者】的区别:
//     重连者 → 恢复其原有职业, 生死态由状态包 kind=2 决定
//     观战者 → 一律这个中立职业 + 固定为灵魂(本文件负责)
//
// ▸ 为什么必须是独立的中立阵营
//   观战者绝不能影响任何一方的胜负判定。三重保证:
//     ① 独立中立阵营 ObsSpectatorTeam —— 不属于船员/内鬼/任何现成阵营,
//        因此不会被算进任一方的存活计数与胜负判定;
//     ② 从不声明获胜机会 —— 该阵营永远没有获胜路径;
//     ③ 观战者本人始终是灵魂 —— 死人不计入存活方的胜负判定。
//
//   职业本身是空壳: 无技能、无胜利条件, 只作为"观战中"的中立标记。
//   名牌后的「（观战中）」对全场可见 —— 由房主把职业下发给全场实现, 见 SpectatorSeat。
//
// ▸ 分配方式
//   withAssignmentOption: false —— 不进入常规职业随机分配,
//   只由插件在运行时为观战者手动分配。
// ============================================================

namespace NebulaResume;

public class SpectatorRole : DefinedRoleTemplate, DefinedRole, IAssignableDocument
{
    /// <summary>独立中立阵营(灰色, 仅自己可见), 与任何现成阵营都不重叠。</summary>
    private static readonly Nebula.Roles.Team ObsSpectatorTeam = new Nebula.Roles.Team(
        "teams.obsSpectator",
        new Virial.Color(0.62f, 0.68f, 0.78f),
        TeamRevealType.OnlyMe,
        () => 30f);

    private SpectatorRole() : base(
        "obsSpectator",
        ObsSpectatorTeam.Color,
        RoleCategory.NeutralRole,
        ObsSpectatorTeam,
        new IConfiguration[] { },
        withAssignmentOption: false)
    {
    }

    public static SpectatorRole MyRole = new();

    bool IAssignableDocument.HasTips => false;

    RuntimeRole RuntimeAssignableGenerator<RuntimeRole>.CreateInstance(Player player, int[] arguments)
        => new Instance(player);

    // ============================================================
    // Runtime —— 什么都不做的空职业
    // ============================================================
    public class Instance : RuntimeAssignableTemplate, RuntimeRole
    {
        DefinedRole RuntimeRole.Role => MyRole;

        public Instance(Player player) : base(player) { }

        // 无技能、无获胜机会: 从不声明 opportunity → 该阵营永不获胜。
        void RuntimeAssignable.OnActivated() { }

        /// <summary>
        ///     名牌后追加「（观战中）」。
        ///
        ///   ▸ 为什么这里【不带任何可见性条件】
        ///     这个回调在每台客户端上都会为自己的那份职业实例调用一次;
        ///     只要职业已经下发到那台客户端, 就会无条件追加标签。
        ///     观战者是公开身份(中立、不参与胜负), 让全场知道他在观战,
        ///     反而能省掉"这人是死是活"的困惑 —— 所以不加条件。
        ///
        ///   ▸ 前提: 职业必须真的下发到全场
        ///     只在【本机】装职业(InvokeLocal)时, 别人的客户端里这个玩家没有这个职业,
        ///     连"要不要装饰"都无从判断 —— 标签就只在自己屏幕上出现。
        ///     下发动作见 SpectatorSeat.TickHostRoleBroadcast。
        /// </summary>
        void RuntimeAssignable.DecorateNameConstantly(ref string name, bool canSeeAllInfo, bool inEndScene)
        {
            name += " " + SpectatorTag;
        }

        /// <summary>名牌后缀, 形如「（观战中）」; 语言文件缺失时回退到中文。</summary>
        private static string _spectatorTag;

        internal static string SpectatorTag
        {
            get
            {
                if (_spectatorTag != null)
                {
                    return _spectatorTag;
                }

                var name = "观战中";

                try
                {
                    var t = NebulaAPI.Language.Translate("role.obsSpectator.name");
                    if (!string.IsNullOrEmpty(t) && !t.StartsWith("*") && !t.StartsWith("role."))
                    {
                        name = t;
                    }
                }
                catch { }

                _spectatorTag = "（" + name + "）";
                return _spectatorTag;
            }
        }
    }
}

/// <summary>
///     在 Roles 阶段提前实例化观战职业。
///
///     职业列表在预处理阶段就会定型并分配编号, 之后不再接受新职业。
///     如果等到运行时第一次访问 MyRole 才创建, 这个职业就不在列表里,
///     分配时按索引找不到 → 越界异常, 观战者只能退回普通职业。
///     所以这里提前触发一次静态初始化。
/// </summary>
[NebulaPreprocess(PreprocessPhase.Roles)]
public static class SpectatorRoleRegister
{
    public static void Preprocess(NebulaPreprocessor pp)
    {
        try
        {
            var role = SpectatorRole.MyRole;

            NebulaResumeEntry.Log("观战职业: 已提前注册"
                + (role != null ? "(实例有效)" : "(实例为空!)") + "。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("观战职业: 提前注册失败: " + e.GetType().Name + " : " + e.Message);
        }
    }
}
