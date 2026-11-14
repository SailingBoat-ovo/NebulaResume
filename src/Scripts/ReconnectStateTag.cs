// ============================================================
// ReconnectStateTag.cs — 结算界面里的"已重连"状态
//
// ▸ 背景
//   玩家掉线时, 模组会把自己的状态标记为「掉线」。
//   重连回来之后如果没有改回来, 结算界面里这个人会一直显示成「掉线」,
//   看起来像是没回来过。
//
// ▸ 做法
//   模组的状态标签(PlayerState)是一组可扩展的文本标签, 文本来自翻译表。
//   所以这里:
//     ① 新建一个状态标签 state.reconnected
//     ② 给它注册一条翻译("已重连")
//     ③ 重连成功时把玩家状态从「掉线」改成这个标签
//
// ▸ 注意
//   标签必须在预处理阶段创建 —— 模组会在这个阶段给所有标签分配编号,
//   之后再创建会因为序号不同步而显示错乱。
// ============================================================

using System.Reflection;
using Virial.Attributes;

namespace NebulaResume;

[NebulaPreprocess(PreprocessPhase.FixStructure)]
public static class ReconnectStateTag
{
    /// <summary>自定义状态标签: 已重连。</summary>
    public static Nebula.Utilities.TranslatableTag Reconnected =
        new Nebula.Utilities.TranslatableTag("state.reconnected");

    private static bool _registered;

    static void Preprocess(NebulaPreprocessor pp)
    {
        try
        {
            // 访问静态字段触发标签创建(必须在预处理阶段完成)。
            var tag = Reconnected;

            // 注册翻译。翻译器在模组内部, 这里通过反射调用它的注册入口。
            _registered = RegisterTranslation("state.reconnected", "已重连");

            NebulaResumeEntry.Log("状态标签: 已创建 state.reconnected, 翻译注册 = " + _registered);
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("状态标签: 创建失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>注册一条函数式翻译。找不到入口时返回 false。</summary>
    private static bool RegisterTranslation(string key, string text)
    {
        try
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type? t = null;

                try { t = asm.GetType("Nebula.Modules.Language", false); } catch { }

                if (t == null)
                {
                    continue;
                }

                var m = t.GetMethod("Register",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(System.Func<string>) },
                    null);

                if (m != null)
                {
                    System.Func<string> fn = () => text;
                    m.Invoke(null, new object[] { key, fn });
                    return true;
                }
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("状态标签: 注册翻译异常: " + e.Message);
        }

        return false;
    }

    /// <summary>
    ///     把一名玩家的状态从「掉线」改成「已重连」。
    ///
    ///     同时清掉 IsDisconnected —— 它是 IsDead 的前置条件,
    ///     不清的话这个人会一直被当成死者处理。
    /// </summary>
    internal static void MarkReconnected(Virial.Game.Player? nosPlayer)
    {
        if (nosPlayer == null)
        {
            return;
        }

        try
        {
            var t = nosPlayer.GetType();

            // ① 清掉"已断开"标记
            var disc = t.GetProperty("IsDisconnected",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (disc != null && disc.CanWrite)
            {
                disc.SetValue(nosPlayer, false);
            }
            else
            {
                t.GetField("<IsDisconnected>k__BackingField",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(nosPlayer, false);
            }

            // ② 把状态标签换成"已重连"
            if (_registered && Reconnected != null)
            {
                var state = t.GetProperty("MyState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (state != null)
                {
                    if (state.CanWrite)
                    {
                        state.SetValue(nosPlayer, Reconnected);
                    }
                    else
                    {
                        state.GetSetMethod(true)?.Invoke(nosPlayer, new object[] { Reconnected });
                    }
                }

                NebulaResumeEntry.Log("状态标签: 玩家 " + nosPlayer.PlayerId + " 的状态已改为「已重连」。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("状态标签: 写入失败: " + e.GetType().Name + " : " + e.Message);
        }
    }
}
