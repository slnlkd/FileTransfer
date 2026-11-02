using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Serilog;
using Transfer.Core;
using Transfer.Core.Remote;

namespace RemoteFiles.Server;
public partial class MainWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<RemoteTransferProgress> transfers = [];
    private RemoteFileService? service;
    private bool closing;
    private bool allowClose;
    private bool changing;
    private readonly string settings;
    private sealed record Settings(string Root, int Port);
    public MainWindow() : this(AppLogging.GetSettingsPath("remote-server.json")) { }
    public MainWindow(string settingsPath)
    {
        settings = settingsPath; InitializeComponent(); TransferGrid.ItemsSource = transfers;
        try
        {
            if (File.Exists(settings) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings)) is { } saved)
            { RootBox.Text = saved.Root; PortBox.Text = saved.Port.ToString(); }
            Log.Information("远程文件服务端窗口创建；配置 {SettingsPath}，根目录 {Root}", settings, RootBox.Text);
        }
        catch (Exception ex) { Log.Warning(ex, "读取远程文件服务配置失败；路径 {SettingsPath}", settings); }
        Closing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; IsEnabled = false;
            await Task.Yield();
            try
            {
                while (changing) await Task.Delay(30);
                if (service != null) await service.DisposeAsync();
                Log.Information("远程文件管理窗口关闭，监听和请求已释放");
            }
            catch (Exception ex) { Log.Error(ex, "关闭远程文件服务失败"); }
            finally { allowClose = true; Close(); }
        };
    }
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择允许远程读写的根目录" };
        if (dialog.ShowDialog() == true) { RootBox.Text = dialog.FolderName; Log.Information("选择远程管理根目录；路径 {Root}", dialog.FolderName); }
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (changing || closing) return;
        changing = true; StartButton.IsEnabled = false; ChooseButton.IsEnabled = false; PortBox.IsEnabled = false;
        try
        {
            if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) throw new IOException("端口请输入 1～65535");
            if (service != null) { await service.DisposeAsync(); service = null; }
            service = new RemoteFileService(RootBox.Text);
            transfers.Clear();
            service.TransferProgress += progress => Dispatcher.BeginInvoke(() =>
            {
                int index = -1;
                for (int i = 0; i < transfers.Count; i++) if (transfers[i].RequestId == progress.RequestId) { index = i; break; }
                if (index >= 0) transfers[index] = progress;
                else { if (transfers.Count >= 200) transfers.RemoveAt(0); transfers.Add(progress); }
            });
            service.Activity += message => Dispatcher.BeginInvoke(() => { ActivityBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n"); ActivityBox.ScrollToEnd(); });
            service.Start(port);
            bool settingsSaved = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new Settings(service.Store.Root, port))); }
            catch (Exception ex) { settingsSaved = false; Log.Warning(ex, "保存服务端配置失败；路径 {SettingsPath}", settings); }
            StopButton.IsEnabled = true;
            StatusText.Text = $"正在监听 TCP {port}；地址：" + string.Join("、", Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) + (settingsSaved ? "" : "；配置保存失败，下次需重新设置。");
            await Browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动远程文件服务失败；根目录 {Root}，端口输入 {PortInput}", RootBox.Text, PortBox.Text);
            StatusText.Text = ex.Message;
            if (service != null) { await service.DisposeAsync(); service = null; }
            Browser.Disconnect(); StopButton.IsEnabled = false; StartButton.IsEnabled = true; ChooseButton.IsEnabled = true; PortBox.IsEnabled = true;
        }
        finally { changing = false; }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (changing || closing || service == null) return;
        changing = true; StopButton.IsEnabled = false;
        try
        {
            await service.StopAsync(); Browser.Disconnect();
            StatusText.Text = "服务已停止；选定根目录保留。已启动的被管理程序继续运行。";
            StartButton.IsEnabled = true; ChooseButton.IsEnabled = true; PortBox.IsEnabled = true;
        }
        catch (Exception ex) { Log.Error(ex, "停止远程文件管理服务失败"); StatusText.Text = ex.Message; }
        finally { changing = false; }
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppLogging.LogDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "打开日志目录失败"); StatusText.Text = ex.Message; }
    }
}



