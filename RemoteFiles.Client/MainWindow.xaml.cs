using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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
        SetConnectionState("未连接", ThemeBrush("MutedBrush"));
        SetStatus("就绪", ThemeBrush("MutedBrush"));
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
        if (connecting || Browser.IsBusy)
        {
            SetStatus("当前操作进行中，请稍后再连接。", ThemeBrush("WarningBrush"));
            return;
        }
        connecting = true; ConnectButton.IsEnabled = false; Browser.IsEnabled = false;
        SetConnectionState("连接中…", ThemeBrush("WarningBrush"));
        SetStatus("正在连接服务端…", ThemeBrush("WarningBrush"));
        try
        {
            if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) throw new IOException("端口请输入 1～65535");
            string host = HostBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(host)) throw new IOException("请输入服务端 IP");
            await Browser.ConnectAsync(new RemoteFileClient(host, port));
            bool settingsSaved = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new Settings(host, port))); }
            catch (Exception ex) { settingsSaved = false; Log.Warning(ex, "保存客户端配置失败；路径 {SettingsPath}", settings); }
            ConnectButton.Content = "重新连接";
            SetConnectionState("已连接", ThemeBrush("SuccessBrush"));
            SetStatus($"已连接 {host}:{port}。" + (settingsSaved ? "" : " 配置保存失败，下次需重新输入地址。"),
                settingsSaved ? ThemeBrush("SuccessBrush") : ThemeBrush("WarningBrush"));
            Log.Information("远程文件客户端连接成功；Host {Host}，Port {Port}", host, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "远程文件客户端连接失败");
            ConnectButton.Content = "连接";
            SetConnectionState("未连接", ThemeBrush("MutedBrush"));
            SetStatus("连接失败：" + ex.Message, ThemeBrush("DangerBrush"));
            Browser.Disconnect();
        }
        finally { connecting = false; ConnectButton.IsEnabled = true; Browser.IsEnabled = true; }
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppLogging.LogDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "打开日志目录失败"); SetStatus(ex.Message, ThemeBrush("DangerBrush")); }
    }
    private void SetConnectionState(string text, Brush dot)
    {
        ConnectionText.Text = text;
        ConnectionDot.Fill = dot;
        ConnectButton.Style = (Style)FindResource(text == "已连接" ? "SecondaryButton" : "PrimaryButton");
    }
    private void SetStatus(string text, Brush dot)
    {
        StatusText.Text = text;
        StatusDot.Fill = dot;
    }
    private Brush ThemeBrush(string key) => (Brush)FindResource(key);
}
