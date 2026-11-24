using Transfer.Desktop;
using System.IO;
using System.Windows;
using Serilog;

namespace RemoteFiles.Client;

public partial class App : LoggedApplication
{
    public App() : base("RemoteFiles.Client") { }

    protected override void OnStartup(StartupEventArgs e)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            MigrateLegacySettings(Path.Combine(localAppData, "FileTransferDemo"), AppContext.BaseDirectory);
        base.OnStartup(e);
    }

    internal static void MigrateLegacySettings(string sourceDirectory, string targetDirectory)
    {
        string marker = Path.Combine(targetDirectory, ".legacy-client-settings-migrated");
        if (File.Exists(marker) || !Directory.Exists(sourceDirectory)) return;

        bool copiedAll = true;
        foreach (string name in new[] { "remote-client.json", "theme.json", "layout-remote-client.json", "operator.json" })
        {
            string source = Path.Combine(sourceDirectory, name);
            string target = Path.Combine(targetDirectory, name);
            if (File.Exists(target) || !File.Exists(source)) continue;

            string staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, staging);
                File.Move(staging, target);
                Log.Information("已迁移旧版客户端配置；文件 {SettingName}", name);
            }
            catch (IOException) when (File.Exists(target))
            {
                // 另一个客户端启动时可能已经完成了此文件的迁移。
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                copiedAll = false;
                Log.Warning(ex, "迁移客户端配置失败，下次启动重试；文件 {SettingName}", name);
            }
            finally
            {
                try { File.Delete(staging); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Debug(ex, "未能清理配置迁移临时文件；文件 {SettingName}", name);
                }
            }
        }

        if (!copiedAll) return;
        try { File.WriteAllText(marker, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "未能保存客户端配置迁移状态");
        }
    }
}
