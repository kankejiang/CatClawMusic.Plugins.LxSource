using System.Text.Json;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>LX 源插件配置（JSON 持久化到宿主数据目录）</summary>
public class LxConfig
{
    /// <summary>lx-music 自定义源 .js 在线地址（空 = 未在线导入）</summary>
    public string ScriptUrl { get; set; } = "";

    /// <summary>lx-music 自定义源 .js 本地文件路径（空 = 未本地导入）</summary>
    public string ScriptFilePath { get; set; } = "";

    /// <summary>音质档位：0=标准 128k，1=高品 320k，2=无损 FLAC</summary>
    public int QualityLevel { get; set; } = 1;

    /// <summary>默认音源标识（空 = 自动，用脚本声明的第一个源；如 netease/qq/kuwo/kugou/migu）</summary>
    public string DefaultSource { get; set; } = "";
}

/// <summary>配置读写：{LocalApplicationData}/CatClawMusic.Maui/lx_source_config.json</summary>
public static class LxConfigStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatClawMusic.Maui");

    private static readonly string FilePath = Path.Combine(Dir, "lx_source_config.json");

    /// <summary>读取配置（文件缺失/损坏时返回默认值）</summary>
    public static LxConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<LxConfig>(File.ReadAllText(FilePath));
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new LxConfig();
    }

    /// <summary>写入配置（静默失败，不阻塞 UI）</summary>
    public static void Save(LxConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg));
        }
        catch
        {
        }
    }
}
