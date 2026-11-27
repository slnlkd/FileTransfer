using System.IO;
using System.Text.Json;
using Serilog;
using Transfer.Core;

namespace RemoteFiles.Server;

internal sealed record NgFilterSettings(string ConnectionString, int CommandTimeoutSeconds = 10,
    bool ExposeAnnotationSqlTemplates = false)
{
    public int AnnotationTimeout => Math.Clamp(CommandTimeoutSeconds <= 0 ? 10 : CommandTimeoutSeconds, 1, 60);

    public static NgFilterSettings? Load()
    {
        string path = AppLogging.GetSettingsPath("ngfilter.json");
        try
        {
            if (!File.Exists(path)) { Log.Warning("只看NG配置不存在；路径 {SettingsPath}", path); return null; }
            var settings = JsonSerializer.Deserialize<NgFilterSettings>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(settings?.ConnectionString))
            {
                Log.Warning("只看NG配置缺少连接字符串；路径 {SettingsPath}", path);
                return null;
            }
            Log.Information("只看NG配置已加载；路径 {SettingsPath}", path);
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 NG 配置失败；路径 {SettingsPath}", path);
            return null;
        }
    }
}
