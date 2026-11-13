using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Serilog;
using Transfer.Core;
using Transfer.Core.Remote;
using Transfer.Desktop;

namespace RemoteFiles.Client;
public partial class MainWindow : Window
{
    private readonly string settings;
    private bool connecting;
    private bool checkingUpdate;
    private string connectedHost = "";
    private int connectedPort;
    private sealed record Settings(string Host, int Port);
    public MainWindow() : this(AppLogging.GetSettingsPath("remote-client.json")) { }
    public MainWindow(string settingsPath)
    {
        settings = settingsPath; InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
        Title = $"远程文件管理 · 客户端 v{version.ToString(3)}";
        if (Browser.FindName("FileGrid") is DataGrid fileGrid) WindowLayout.Attach(this, "remote-client", fileGrid);
        Browser.StatusChanged += (text, key) => SetStatus(text, ThemeBrush(key));
        Browser.ProgressChanged += UpdateBrowserProgress;
        Browser.SelectionCountChanged += UpdateSelection;
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
            connectedHost = host; connectedPort = port;
            bool settingsSaved = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new Settings(host, port))); }
            catch (Exception ex) { settingsSaved = false; Log.Warning(ex, "保存客户端配置失败；路径 {SettingsPath}", settings); }
            ConnectButton.Content = "重新连接";
            SetConnectionState("已连接", ThemeBrush("SuccessBrush"));
            if (Browser.ProtocolWarning is { } protocolWarning)
                SetStatus(protocolWarning, ThemeBrush("WarningBrush"));
            else
                SetStatus($"已连接 {host}:{port}。" + (settingsSaved ? "" : " 配置保存失败，下次需重新输入地址。"),
                    settingsSaved ? ThemeBrush("SuccessBrush") : ThemeBrush("WarningBrush"));
            _ = CheckForUpdatesAsync(true);
            Log.Information("远程文件客户端连接成功；Host {Host}，Port {Port}", host, port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "远程文件客户端连接失败");
            Browser.Disconnect();
            ConnectButton.Content = "连接";
            SetConnectionState("未连接", ThemeBrush("MutedBrush"));
            SetStatus("连接失败：" + ex.Message, ThemeBrush("DangerBrush"));
        }
        finally { connecting = false; ConnectButton.IsEnabled = true; Browser.IsEnabled = true; }
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(false);
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (checkingUpdate || connecting || Browser.IsBusy) return;
        if (connectedPort == 0)
        {
            if (!silent) MessageBox.Show(this, "请先连接服务端。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        checkingUpdate = true; CheckUpdateButton.IsEnabled = false;
        try
        {
            var endpoint = new RemoteFileClient(connectedHost, connectedPort);
            var info = await endpoint.CheckUpdateAsync();
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
            if (!IsNewer(info.Version, current))
            {
                if (!silent) MessageBox.Show(this, $"当前已是最新版本 v{current.ToString(3)}。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n更新说明：\n" + info.Notes;
            var answer = MessageBox.Show(this,
                $"发现新版本 v{info.Version}\n当前版本 v{current.ToString(3)}{notes}\n\n更新完成后客户端会自动重启。是否现在更新？",
                "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            await ApplyUpdateAsync(endpoint, info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查更新失败");
            if (!silent) MessageBox.Show(this, "检查更新失败：" + ex.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { checkingUpdate = false; CheckUpdateButton.IsEnabled = true; }
    }
    private static bool IsNewer(string latestText, Version current)
    {
        if (!Version.TryParse(latestText, out var latest)) return false;
        int latestBuild = Math.Max(0, latest.Build), currentBuild = Math.Max(0, current.Build);
        return latest.Major > current.Major
            || latest.Major == current.Major && (latest.Minor > current.Minor
                || latest.Minor == current.Minor && latestBuild > currentBuild);
    }
    private async Task ApplyUpdateAsync(RemoteFileClient endpoint, RemoteUpdateInfo info)
    {
        string versionTag = string.Concat(info.Version.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));
        string tempRoot = Path.Combine(Path.GetTempPath(), "FileTransferUpdate", versionTag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var progressWindow = new UpdateProgressWindow { Owner = this };
        progressWindow.Show();
        try
        {
            string zip = Path.Combine(tempRoot, info.FileName);
            var progress = new Progress<double>(value => progressWindow.Report($"正在下载 v{info.Version}… {value:F0}%", value));
            await endpoint.DownloadUpdateAsync(info, zip, progress);
            progressWindow.Report("正在解压更新包…", 100);
            string staging = Path.Combine(tempRoot, "app");
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zip, staging, true);
            string exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "RemoteFiles.Client.exe");
            if (!File.Exists(Path.Combine(staging, exeName)))
            {
                var directories = Directory.GetDirectories(staging);
                if (directories.Length == 1 && File.Exists(Path.Combine(directories[0], exeName))) staging = directories[0];
                else throw new IOException("更新包结构无效，未找到 " + exeName);
            }
            progressWindow.Report("正在准备重启…", 100);
            string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string script = Path.Combine(tempRoot, "apply-update.ps1");
            // PowerShell 5.1 只有带 BOM 才会按 UTF-8 读取脚本，否则日志中的中文会乱码。
            File.WriteAllText(script, UpdaterScript, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(), "-Staging", staging, "-Target", target, "-Exe", exeName, "-TempRoot", tempRoot })
                start.ArgumentList.Add(argument);
            Process.Start(start);
            progressWindow.Close();
            Application.Current.Shutdown();
        }
        catch
        {
            progressWindow.Close();
            throw;
        }
    }
    private const string UpdaterScript = """
param([int]$ProcessId,[string]$Staging,[string]$Target,[string]$Exe,[string]$TempRoot)
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:LOCALAPPDATA 'FileTransferDemo\Logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'update.log'
function Write-UpdateLog([string]$Message) { [IO.File]::AppendAllText($log, "$(Get-Date -Format o) $Message`r`n", (New-Object Text.UTF8Encoding($false))) }
$backup = "$Target.__old"
try {
    Write-UpdateLog "等待客户端退出：$ProcessId"
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Milliseconds 800
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    try {
        Rename-Item -LiteralPath $Target -NewName (Split-Path -Leaf $backup)
        try {
            Move-Item -LiteralPath $Staging -Destination $Target
        } catch {
            Write-UpdateLog "替换文件失败：$_"
            Rename-Item -LiteralPath $backup -NewName (Split-Path -Leaf $Target)
            throw
        }
    } catch {
        # 目录被占用时（例如仍作为工作目录），退化为逐文件覆盖。
        Write-UpdateLog "整体替换失败，改为逐文件覆盖：$_"
        Copy-Item -Path (Join-Path $Staging '*') -Destination $Target -Recurse -Force
    }
    Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
    Write-UpdateLog "更新完成并已重启"
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    Write-UpdateLog "更新失败：$_"
    if (Test-Path -LiteralPath (Join-Path $Target $Exe)) { Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target }
} finally {
    Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";
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
        StatusText.ToolTip = text;
        StatusDot.Fill = dot;
    }
    private void UpdateBrowserProgress(string text, double percent, bool indeterminate, bool visible)
    {
        BrowserProgressPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        BrowserProgressText.Text = text;
        BrowserProgressText.ToolTip = text;
        BrowserProgress.IsIndeterminate = indeterminate;
        if (!indeterminate) BrowserProgress.Value = Math.Clamp(percent, 0, 100);
    }
    private void UpdateSelection(int selected, int total)
    {
        SelectionText.Text = selected > 0 ? $"已选择 {selected} 项 / 共 {total} 项" : "";
        SelectionText.ToolTip = SelectionText.Text;
    }
    private Brush ThemeBrush(string key) => (Brush)FindResource(key);
}
