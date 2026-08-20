using System.Reflection;
using System.Runtime.CompilerServices;

// ── 模拟 CatClawMusic 宿主插件导入路径，复现 Jint 加载失败 ──
// 宿主行为（见 PluginManager.LoadAndRegisterPluginAsync）：
//   Assembly.Load(bytes) → GetTypes() → CreatePluginInstances → InitializeAsync()
// 本程序不引用 Jint/Acornima，Jint 必须靠插件自身的 AssemblyResolve 从嵌入资源加载。

var pluginPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "bin", "Release", "net10.0", "CatClawMusic.Plugins.LxSource.dll"));
Console.WriteLine($"[1] 插件路径: {pluginPath}");
if (!File.Exists(pluginPath)) { Console.WriteLine("未找到插件 DLL，先执行 Release 构建"); return 1; }

// 模拟宿主 PluginManager 的全局 AssemblyResolve（仅解析宿主程序集，找不到返回 null）
var coreAsm = typeof(CatClawMusic.Core.Models.OnlineSong).Assembly;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
    new AssemblyName(e.Name).Name == coreAsm.GetName().Name ? coreAsm : null;

// 1) Assembly.Load(byte[])
var fileBytes = File.ReadAllBytes(pluginPath);
Assembly asm;
try
{
    asm = Assembly.Load(fileBytes);
    Console.WriteLine($"[2] Assembly.Load(bytes) OK: {asm.FullName}");
}
catch (Exception ex)
{
    Console.WriteLine($"[2] Assembly.Load(bytes) 失败: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

// 嵌入资源清单
Console.WriteLine("[3] 嵌入资源:");
foreach (var r in asm.GetManifestResourceNames())
    Console.WriteLine($"    {r}");

// 3.1) 模拟宿主修复：GetTypes() 前强制运行模块初始化器（RunModuleConstructor），
//     确保插件内部 AssemblyResolve 先注册，GetTypes() 扫描 Jint 引用类型时才不会失败。
// 注：默认关闭（= 旧宿主行为，用于复现）；设为 true 验证修复。
const bool SimulateHostFix = true;
if (SimulateHostFix)
{
    Console.WriteLine("[3.1] 模拟宿主修复 RunModuleConstructor...");
    RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);
    Console.WriteLine("    完成");
}

// 2) GetTypes()
Console.WriteLine("[4] GetTypes():");
Type[] types;
try
{
    types = asm.GetTypes();
    Console.WriteLine($"    全部类型加载成功，共 {types.Length} 个");
}
catch (ReflectionTypeLoadException rtle)
{
    types = rtle.Types.Where(t => t != null).ToArray()!;
    Console.WriteLine($"    ReflectionTypeLoadException：{rtle.LoaderExceptions.Length} 个类型失败，成功 {types.Length} 个");
    foreach (var le in rtle.LoaderExceptions.Where(e => e != null).Take(8))
        Console.WriteLine($"      LoaderException: {le!.GetType().Name}: {le.Message}");
}

// 3) 模拟宿主 AssemblyResolve 手工解析 Jint（验证插件处理器是否有效）
// 注意：此步不再手工调用 Register()，以验证"CreateInstance 是否自动触发模块初始化器"
Console.WriteLine("[5] 直接测试 Jint 解析（不手工 Register，验证模块初始化器是否被 CreateInstance 触发）:");
try
{
    var jint = Assembly.Load(new AssemblyName("Jint, Version=4.16.0.0, Culture=neutral, PublicKeyToken=2e92ba9c8d81157f"));
    Console.WriteLine($"    Jint 加载成功: {jint.FullName} （模块初始化器已被触发）");
}
catch (Exception ex)
{
    Console.WriteLine($"    Jint 加载失败: {ex.GetType().Name}: {ex.Message} （模块初始化器未被触发）");
}

// 4) 模拟 CreatePluginInstances：反射创建 LxMusicPlugin + InitializeAsync
Console.WriteLine("[6] 反射创建 LxMusicPlugin:");
var pluginType = types.FirstOrDefault(t => t?.Name == "LxMusicPlugin");
if (pluginType == null) { Console.WriteLine("    未找到 LxMusicPlugin"); return 1; }
object? plugin;
try
{
    plugin = Activator.CreateInstance(pluginType);
    Console.WriteLine($"    Activator.CreateInstance OK: {plugin?.GetType().FullName}");
}
catch (Exception ex)
{
    Console.WriteLine($"    Activator.CreateInstance 失败: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"      Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    return 1;
}

try
{
    var init = pluginType.GetMethod("InitializeAsync");
    if (init != null)
    {
        var t = (Task)init.Invoke(plugin, null)!;
        t.GetAwaiter().GetResult();
        Console.WriteLine("    InitializeAsync OK");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    InitializeAsync 失败: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"      Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
}

// 5) 尝试真正用 Jint（脚本引擎），验证全链路
Console.WriteLine("[7] 完整脚本链路（LxScriptHost 内 new Engine）：");
try
{
    var hostType = types.FirstOrDefault(t => t?.Name == "LxScriptHost");
    var host = Activator.CreateInstance(hostType!)!;
    var runAsync = hostType!.GetMethod("RunAsync");
    var t2 = (Task<bool>)runAsync!.Invoke(host, new object[] { "send(globalThis.lx && lx.EVENT_NAMES ? 'inited' : '', {});" })!;
    var ok = t2.GetAwaiter().GetResult();
    Console.WriteLine($"    RunAsync 执行结果: {ok}");
}
catch (Exception ex)
{
    Console.WriteLine($"    脚本链路失败: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"      Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    // 检查依赖链：Jint 加载了，但 Jint 的依赖是否都在？
    Console.WriteLine("    已加载程序集（含 Jint 相关）:");
    foreach (var a in AppDomain.CurrentDomain.GetAssemblies()
                 .Where(a => a.GetName().Name!.Contains("Jint") || a.GetName().Name!.Contains("Acornima")
                             || a.GetName().Name!.Contains("Esprima") || a.GetName().Name!.Contains("Newtonsoft")))
        Console.WriteLine($"      {a.FullName}");
}

return 0;
