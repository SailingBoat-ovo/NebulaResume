// ============================================================
// ResumeDriver.cs — 用 MonoBehaviour 驱动采集循环
//
// 为什么用这个而不是 NebulaAPI.RegisterExtension<T, GameUpdateEvent>:
//   实机测试证明 RegisterExtension 这条路在本插件里【没有生效】——
//   注册调用成功返回, 但回调一次都没被触发(日志中心跳行完全缺失)。
//   MonoBehaviour 是本机已经被验证可行的驱动方式:
//   (此处原有一句外部模块名的说明, 已移除)
//   ClassInjector.RegisterTypeInIl2Cpp<>() + GameObject.AddComponent<>()
//   驱动它的 Update() 循环的。
//
// ⚠️ IL2CPP 下注入 MonoBehaviour 的构造函数必须带 IntPtr 参数。
// ============================================================

using Il2CppInterop.Runtime.Injection;

namespace NebulaResume;

public class ResumeDriver : MonoBehaviour
{
    public ResumeDriver(System.IntPtr ptr) : base(ptr)
    {
    }

    private void Update()
    {
        NebulaResumeEntry.Tick();
    }

    /// <summary>
    ///     创建常驻驱动对象。失败只记日志, 不影响游戏 —— 采集停了但游戏照常可玩。
    /// </summary>
    internal static void Install()
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ResumeDriver>();

            var go = new GameObject("NebulaResumeDriver");
            // 必须限定命名空间: global using System 与 UnityEngine 同时存在时,
            // 裸写 Object 会 CS0104 歧义(会导致整个 addon 编译失败)。
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ResumeDriver>();

            NebulaResumeEntry.Log("驱动对象已创建 (MonoBehaviour)");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("驱动对象创建失败: " + e.GetType().Name + " : " + e.Message);
        }
    }
}
