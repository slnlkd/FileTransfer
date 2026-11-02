using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Serilog;
using Transfer.Core;
using Transfer.Core.Remote;

namespace RemoteFiles.Client;
public partial class MainWindow : Window
{
    private readonly string settings;
    private bool connecting;
    private sealed record Settings(string Host, int Port);
    public MainWindow() : this(AppLogging.GetSettingsPath("remote-client.json")) { }
    public MainWindow(string settingsPath)
    {
        settings = settingsPath; InitializeComponent();
        try
        {
            if (File.Exists(settings) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings)) is { } saved)
            { HostBox.Text = saved.Host; PortBox.Text = saved.Port.ToString(); }
        }
        catch (Exception ex) { Log.Warning(ex, "读取远程文件客户端配置失败"); }
        Closing += (_, _) => Log.Information("远程文件客户端窗口关闭");
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (connecting || Browser.IsBusy) { StatusText.Text = "当前操作进行中，请稍后再连接。"; return; }
        connecting = true; ConnectButton.IsEnabled = false; Browser.IsEnabled = false;
        try
        {
            if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) throw new IOException("端口请输入 1～65535");
            string host = HostBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(host)) throw new IOException("请输入服务端 IP");
            await Browser.ConnectAsync(new RemoteFileClient(host, port));
            bool settingsSaved = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new Settings(host, port))); }
            catch (Exception ex) { settingsSaved = false; Log.Warning(ex, "保存客户端配置失败；路径 {SettingsPath}", settings); }
            StatusText.Text = $"已连接 {host}:{port}；左侧为目录树，右侧为当前目录。" + (settingsSaved ? "" : "配置保存失败，下次需重新输入地址。");
            Log.Information("远程文件客户端连接成功；Host {Host}，Port {Port}", host, port);
        }
        catch (Exception ex) { Log.Error(ex, "远程文件客户端连接失败"); StatusText.Text = "连接失败：" + ex.Message; Browser.Disconnect(); }
        finally { connecting = false; ConnectButton.IsEnabled = true; Browser.IsEnabled = true; }
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppLogging.LogDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "打开日志目录失败"); StatusText.Text = ex.Message; }
    }
}



