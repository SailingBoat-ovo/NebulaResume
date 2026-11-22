// ============================================================
// ResumeOptions.cs — 重连相关设置项(显示在模组的设置面板里)
//
// ▸ 为什么用设置面板而不是聊天命令
//   游戏内聊天输入框对符号有限制, 自建命令容易出现"发不出去"的情况。
//   模组自带的设置面板可以直接点选, 而且房主的改动会自动同步给全场。
//
// ▸ 注册方式
//   仅创建配置项还不够 —— 必须把它挂到一个配置容器(holder)上,
//   容器再指定显示在哪个分组。这一步要在 FixStructure 阶段完成,
//   那是模组确定数据结构的时间点, 早于设置面板的构建。
//
// ▸ 配置项
//   · 允许断线重连           (默认开, 总开关)
//   · 未死亡重连后保持活人 (默认开)
//   · 每局最多允许重连次数   (默认 3)
//   · 中途加入观战 / 观战人数上限
// ============================================================

using Virial.Attributes;
using Virial.Configuration;

namespace NebulaResume;

internal static class ResumeOptions
{
    /// <summary>总开关: 本房间是否允许断线重连(关掉后掉线即离开, 会议中进入一并关闭)。</summary>
    internal static BoolConfiguration ReconnectEnabled =
        NebulaAPI.Configurations.Configuration(
            "options.resume.enabled",
            true,
            title: NebulaAPI.GUI.LocalizedTextComponent("options.resume.enabled"));

    /// <summary>会议进行中是否允许重连/新玩家进入(需要总开关也开着)。关掉则会议中拒绝进入, 散会后再进。</summary>
    internal static BoolConfiguration MeetingJoinEnabled =
        NebulaAPI.Configurations.Configuration(
            "options.resume.meetingJoin",
            true,
            title: NebulaAPI.GUI.LocalizedTextComponent("options.resume.meetingJoin"));

    /// <summary>未死亡掉线重连后是否保持活人。关掉则转为灵魂。</summary>
    internal static BoolConfiguration ReviveOnReconnect =
        NebulaAPI.Configurations.Configuration(
            "options.resume.revive",
            true,
            title: NebulaAPI.GUI.LocalizedTextComponent("options.resume.revive"));

    /// <summary>同一玩家每局最多允许重连的次数(0~10)。超出后拒绝重连。</summary>
    internal static IntegerConfiguration ReconnectLimit =
        NebulaAPI.Configurations.Configuration(
            "options.resume.limit",
            (0, 10, 1),
            3,
            title: NebulaAPI.GUI.LocalizedTextComponent("options.resume.limit"));

    /// <summary>是否允许新玩家在对局中途以观战身份加入。</summary>
    internal static BoolConfiguration SpectatorEnabled =
        NebulaAPI.Configurations.Configuration(
            "options.resume.spectator",
            false,
            title: NebulaAPI.GUI.LocalizedTextComponent("options.resume.spectator"));

    /// <summary>观战者人数上限(与玩家共用 32 个玩家位)。</summary>
    internal static IntegerConfiguration SpectatorLimit =
        NebulaAPI.Configurations.Configuration(
            "options.resume.spectator.limit",
            (0, 15, 1),
            2,
            title: NebulaAPI.GUI.LocalizedTextComponent("options.resume.spectator.limit"));

    internal static bool ReconnectAllowed => ReconnectEnabled;
    internal static bool MeetingJoinAllowed => ReconnectEnabled && MeetingJoinEnabled;
    internal static bool ReviveEnabled => ReviveOnReconnect;
    internal static int Limit => ReconnectLimit;
    internal static bool SpectatorOn => SpectatorEnabled;
    internal static int SpectatorMax => SpectatorLimit;
}

/// <summary>
///     把上面两项挂到设置面板的「设置」分组里。
///
///     执行时机: FixStructure 阶段 —— 模组此时正在确定配置数据结构,
///     早于设置面板构建, 晚于配置项创建。
/// </summary>
[NebulaPreprocess(PreprocessPhase.FixStructure)]
public static class ResumeOptionsRegister
{
    public static void Preprocess(NebulaPreprocessor pp)
    {
        try
        {
            var holder = NebulaAPI.Configurations.Holder(
                title: NebulaAPI.GUI.RawTextComponent("帆船服断线重连"),
                detail: NebulaAPI.GUI.RawTextComponent("掉线后重新进入本服务器即可回到原对局。"),
                tabs: new[] { ConfigurationTab.Settings },
                gamemodes: GameModes.AllGameModes);

            // 访问静态字段会触发它们的初始化(创建配置项), 再挂进容器。
            holder.AppendConfiguration(ResumeOptions.ReconnectEnabled)
                  .AppendConfiguration(ResumeOptions.MeetingJoinEnabled)
                  .AppendConfiguration(ResumeOptions.ReviveOnReconnect)
                  .AppendConfiguration(ResumeOptions.ReconnectLimit)
                  .AppendConfiguration(ResumeOptions.SpectatorEnabled)
                  .AppendConfiguration(ResumeOptions.SpectatorLimit);

            NebulaResumeEntry.Log("设置项: 已注册到设置面板「帆船服断线重连」(允许断线重连 / 允许会议中重连 / 未死亡重连后保持活人 / 每局最多重连次数 / 观战)。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("设置项: 注册失败: " + e.GetType().Name + " : " + e.Message);
        }
    }
}
