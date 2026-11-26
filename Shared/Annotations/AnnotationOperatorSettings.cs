using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using Transfer.Core;

namespace RemoteFiles.Desktop;

internal static class AnnotationOperatorSettings
{
    public static string Load()
    {
        try
        {
            string path = AppLogging.GetSettingsPath("operator.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<OperatorSettings>(File.ReadAllText(path)) is { } saved)
                return saved.Name;
        }
        catch (Exception ex) { Log.Debug(ex, "读取操作人失败"); }
        return Environment.UserName;
    }

    public static void Save(string name)
    {
        try
        {
            string path = AppLogging.GetSettingsPath("operator.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new OperatorSettings(name)), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Debug(ex, "保存操作人失败"); }
    }

    private sealed record OperatorSettings(string Name);
}
