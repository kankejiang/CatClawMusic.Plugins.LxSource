// 工具项目专用桩：ScriptTest / SmokeTest 直接编译插件源码做离线验证，
// 不依赖 MAUI 宿主，因此没有真实的宿主 DI 容器。
//
// 与 LxMusicPlugin.cs 中的 LxHostServices 保持相同的公开形状（Services / JsRuntime），
// 使 LxScriptEngine.cs 能被链接编译。JsRuntime 恒返回 null，引擎会走
// "JS 运行时不可用——宿主版本过旧" 分支——这正是工具项目期望的行为：
// 测试脚本解析与协议逻辑，不测试需要宿主运行的 JS 执行。
//
// 注意：本文件仅在工具项目中通过 Compile Include 链接，不参与插件本体编译。

using CatClawMusic.Core.Interfaces;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>工具项目用的宿主服务桩：无 MAUI 宿主，JS 运行时恒不可用。</summary>
internal static class LxHostServices
{
    public static IServiceProvider? Services { get; set; }

    public static IJsRuntimeService? JsRuntime => null;
}
