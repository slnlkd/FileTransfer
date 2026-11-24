using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using Transfer.Core;
using Transfer.Core.Remote;
using Transfer.Desktop;
using RemoteFiles.Desktop;

namespace RemoteFiles.Client;
public partial class MainWindow : Window
{
    private readonly string settings;
    private bool connecting;
    private CancellationTokenSource? updateCheck;
    private string connectedHost = "";
    private int connectedPort;
    private string[] recentEndpoints = [];
    private static readonly string[] PresetEndpoints = Enumerable.Range(181, 3)
        .Select(address => $"10.100.40.{address}:9501").ToArray();
    private RemoteUpdateInfo? pendingUpdate;
    // 连接期间每 30 分钟静默复查一次，发现新版本只显示蓝色提示，不打断使用。
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private sealed record Settings(string Host, int Port, string[]? RecentEndpoints = null);
    public MainWindow() : this(AppLogging.GetSettingsPath("remote-client.json")) { }
    public MainWindow(string settingsPath)
    {
        settings = settingsPath; InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
        Title = $"远程文件管理 · 客户端 v{version.ToString(3)}";
        UpdateThemeButton();
        if (Browser.FindName("FileGrid") is DataGrid fileGrid) WindowLayout.Attach(this, "remote-client", fileGrid);
        Browser.StatusChanged += (text, key) => SetStatus(text, ThemeBrush(key));
        Browser.ProgressChanged += UpdateBrowserProgress;
        Browser.SelectionCountChanged += UpdateSelection;
        SetConnectionState("未连接", ThemeBrush("MutedBrush"));
        SetStatus("就绪", ThemeBrush("MutedBrush"));
        string endpoint = PresetEndpoints[0];
        try
        {
            if (File.Exists(settings) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings)) is { } saved)
            {
                string previous = FormatEndpoint(saved.Host ?? "", saved.Port);
                if (TryParseEndpoint(previous, out _, out _)) endpoint = previous;
                recentEndpoints = (saved.RecentEndpoints ?? [])
                    .Prepend(previous)
                    .Where(value => TryParseEndpoint(value, out _, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8).ToArray();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "读取远程文件客户端配置失败"); }
        SetEndpointOptions(endpoint);
        updateTimer.Tick += async (_, _) =>
        {
            if (connectedPort != 0 && pendingUpdate is null) await CheckForUpdatesAsync(true);
        };
        updateTimer.Start();
        Closing += (_, _) =>
        {
            updateCheck?.Cancel();
            Log.Information("远程文件客户端窗口关闭");
        };
    }
    private void SetEndpointOptions(string endpoint)
    {
        EndpointBox.ItemsSource = recentEndpoints.Concat(PresetEndpoints).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        EndpointBox.Text = endpoint;
    }
    private static string FormatEndpoint(string host, int port) =>
        host.Contains(':') ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";

    private static bool TryParseEndpoint(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        string endpoint = text?.Trim() ?? "";
        int separator = endpoint.LastIndexOf(':');
        if (separator <= 0) return false;

        string address = endpoint[..separator].Trim();
        if (address.StartsWith('[') && address.EndsWith(']'))
        {
            host = address[1..^1];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            host = address;
            if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        }
        return int.TryParse(endpoint[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }
    private void EndpointBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || EndpointBox.IsDropDownOpen) return;
        e.Handled = true;
        Connect_Click(ConnectButton, e);
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (connecting || Browser.IsBusy)
        {
            SetStatus("当前操作进行中，请稍后再连接。", ThemeBrush("WarningBrush"));
            return;
        }
        if (!TryParseEndpoint(EndpointBox.Text, out string host, out int port))
        {
            SetStatus("请输入地址和端口，端口范围为 1～65535，例如 10.100.40.181:9501。", ThemeBrush("WarningBrush"));
            EndpointBox.Focus();
            return;
        }
        connecting = true; ConnectButton.IsEnabled = false; EndpointBox.IsEnabled = false; Browser.IsEnabled = false;
        updateCheck?.Cancel();
        updateCheck = null;
        pendingUpdate = null;
        UpdateBadge.Visibility = Visibility.Collapsed;
        SetConnectionState("连接中…", ThemeBrush("WarningBrush"));
        SetStatus("正在连接服务端…", ThemeBrush("WarningBrush"));
        bool connected = false;
        try
        {
            await Browser.ConnectAsync(new RemoteFileClient(host, port));
            connectedHost = host; connectedPort = port;
            string endpoint = FormatEndpoint(host, port);
            recentEndpoints = recentEndpoints.Prepend(endpoint).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            SetEndpointOptions(endpoint);
            bool settingsSaved = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new Settings(host, port, recentEndpoints))); }
            catch (Exception ex) { settingsSaved = false; Log.Warning(ex, "保存客户端配置失败；路径 {SettingsPath}", settings); }
            ConnectButton.Content = "重新连接";
            SetConnectionState("已连接", ThemeBrush("SuccessBrush"));
            if (Browser.ProtocolWarning is { } protocolWarning)
                SetStatus(protocolWarning, ThemeBrush("WarningBrush"));
            else
                SetStatus($"已连接 {endpoint}。" + (settingsSaved ? "" : " 配置保存失败，下次需重新选择或输入。"),
                    settingsSaved ? ThemeBrush("SuccessBrush") : ThemeBrush("WarningBrush"));
            Log.Information("远程文件客户端连接成功；Host {Host}，Port {Port}", host, port);
            connected = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "远程文件客户端连接失败");
            Browser.Disconnect();
            connectedHost = ""; connectedPort = 0;
            ConnectButton.Content = "连接";
            SetConnectionState("未连接", ThemeBrush("MutedBrush"));
            SetStatus("连接失败：" + ex.Message, ThemeBrush("DangerBrush"));
        }
        finally { connecting = false; ConnectButton.IsEnabled = true; EndpointBox.IsEnabled = true; Browser.IsEnabled = true; }
        // 连接状态复位后再静默检查，避免被 connecting 标记直接拦掉。
        if (connected) _ = CheckForUpdatesAsync(true);
    }
    private async void UpdateBadge_Click(object sender, RoutedEventArgs e)
    {
        if (pendingUpdate is null || connectedPort == 0) return;
        string host = connectedHost;
        int port = connectedPort;
        // 点击时重新读一次服务端清单，确保下载的是当前最新版本（而不是上次检查时的旧版本）。
        try
        {
            var latest = await new RemoteFileClient(host, port).CheckUpdateAsync();
            if (connecting || host != connectedHost || port != connectedPort) return;
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
            if (!IsNewer(latest.Version, current))
            {
                pendingUpdate = null;
                UpdateBadge.Visibility = Visibility.Collapsed;
                Dialog.Notify(this, $"当前已是最新版本 v{current.ToString(3)}。", "检查更新");
                return;
            }
            pendingUpdate = latest;
            SetUpdateTooltip(latest);
            await PromptUpdateAsync(latest);
        }
        catch (Exception ex)
        {
            if (connecting || host != connectedHost || port != connectedPort) return;
            Log.Warning(ex, "点击更新时检查最新版本失败");
            Dialog.Notify(this, "检查更新失败：" + ex.Message, "检查更新", DialogTone.Warning);
        }
    }
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }
    private void UpdateThemeButton()
    {
        ThemeIcon.Data = (Geometry)FindResource(ThemeManager.IsDark ? "IconSunGeometry" : "IconMoonGeometry");
        ThemeButton.ToolTip = ThemeManager.IsDark ? "切换到日间模式" : "切换到夜间模式";
    }
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (updateCheck is not null || connecting || connectedPort == 0) return;
        using var check = new CancellationTokenSource();
        updateCheck = check;
        try
        {
            var info = await new RemoteFileClient(connectedHost, connectedPort).CheckUpdateAsync(check.Token);
            if (check.IsCancellationRequested) return;
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
            if (!IsNewer(info.Version, current))
            {
                pendingUpdate = null; UpdateBadge.Visibility = Visibility.Collapsed;
                if (!silent) Dialog.Notify(this, $"当前已是最新版本 v{current.ToString(3)}。", "检查更新");
                return;
            }
            pendingUpdate = info;
            SetUpdateTooltip(info);
            UpdateBadge.Visibility = Visibility.Visible;
            Log.Information("发现客户端新版本；当前 {Current}，最新 {Latest}", current.ToString(3), info.Version);
            if (!silent) await PromptUpdateAsync(info);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (silent) Log.Debug(ex, "自动检查更新失败");
            else
            {
                Log.Warning(ex, "检查更新失败");
                Dialog.Notify(this, "检查更新失败：" + ex.Message, "检查更新", DialogTone.Warning);
            }
        }
        finally { if (ReferenceEquals(updateCheck, check)) updateCheck = null; }
    }
    private void SetUpdateTooltip(RemoteUpdateInfo info)
    {
        UpdateBadge.ToolTip = new ToolTip
        {
            Content = new TextBlock
            {
                Text = $"发现新版本 v{info.Version}，点击更新"
                    + (string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n" + info.Notes),
                MaxWidth = 380,
                TextWrapping = TextWrapping.Wrap
            },
            PlacementTarget = UpdateBadge,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
            [
                new(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Vertical),
                new(new Point(target.Width - popup.Width, -popup.Height - 4), PopupPrimaryAxis.Vertical)
            ]
        };
    }

    private async Task PromptUpdateAsync(RemoteUpdateInfo info)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
        string notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n更新说明：\n" + info.Notes;
        if (!Dialog.Confirm(this,
            $"发现新版本 v{info.Version}\n当前版本 v{current.ToString(3)}{notes}\n\n更新完成后客户端会自动重启。是否现在更新？",
            "检查更新")) return;
        try
        {
            await ApplyUpdateAsync(new RemoteFileClient(connectedHost, connectedPort), info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "客户端更新失败");
            Dialog.Notify(this, "更新失败：" + ex.Message, "检查更新", DialogTone.Warning);
        }
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
        string tempRoot = CreateUpdateTempRoot(versionTag);
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
            string exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "FileTransfer.Client.exe");
            if (!File.Exists(Path.Combine(staging, exeName)))
            {
                var directories = Directory.GetDirectories(staging);
                if (directories.Length == 1 && File.Exists(Path.Combine(directories[0], exeName))) staging = directories[0];
                else
                {
                    // 兼容发布名调整：RemoteFiles.Client.exe 与 FileTransfer.Client.exe 可以互相迁移。
                    string renamed = exeName.Equals("RemoteFiles.Client.exe", StringComparison.OrdinalIgnoreCase)
                        ? "FileTransfer.Client.exe" : "RemoteFiles.Client.exe";
                    if (File.Exists(Path.Combine(staging, renamed))) exeName = renamed;
                    else throw new IOException("更新包结构无效，未找到 " + exeName);
                }
            }
            progressWindow.Report("正在准备重启…", 100);
            string target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            string script = Path.Combine(tempRoot, "apply-update.ps1");
            // PowerShell 5.1 只有带 BOM 才会按 UTF-8 读取脚本，否则日志中的中文会乱码。
            File.WriteAllText(script, UpdaterScript, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
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
            try { Directory.Delete(tempRoot, true); }
            catch (Exception ex) { Log.Debug(ex, "清理更新临时目录失败；路径 {UpdateTemp}", tempRoot); }
            throw;
        }
    }
    private static string CreateUpdateTempRoot(string versionTag)
    {
        string name = versionTag + "-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(AppContext.BaseDirectory, "Update", name);
        Directory.CreateDirectory(path);
        Log.Information("更新临时目录已创建；路径 {UpdateTemp}", path);
        return path;
    }
    private const string UpdaterScript = """
param([int]$ProcessId,[string]$Staging,[string]$Target,[string]$Exe,[string]$TempRoot)
$ErrorActionPreference = 'Stop'
$updateRoot = Join-Path $Target 'Update'
$TempRoot = [IO.Path]::GetFullPath($TempRoot)
$Staging = [IO.Path]::GetFullPath($Staging)
if ([IO.Path]::GetDirectoryName($TempRoot) -ne $updateRoot -or
    -not $Staging.StartsWith($TempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '更新暂存目录不在部署目录下。'
}
$logDir = Join-Path $Target 'Logs'
try { [IO.Directory]::CreateDirectory($logDir) | Out-Null } catch { }
$log = Join-Path $logDir 'update.log'
function Write-UpdateLog([string]$Message) {
    try { [IO.File]::AppendAllText($log, "$(Get-Date -Format o) $Message`r`n", (New-Object Text.UTF8Encoding($false))) } catch { }
}
$backupRoot = Join-Path $TempRoot 'backup'
$applied = [Collections.Generic.Stack[object]]::new()
$keepBackup = $false
$updateLock = $null
try {
    $updateLock = [IO.File]::Open((Join-Path $updateRoot 'update.lock'), 'OpenOrCreate', 'Write', 'None')
    Write-UpdateLog "等待客户端退出：$ProcessId"
    $runningClient = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($runningClient -and -not $runningClient.WaitForExit(120000)) { throw '等待客户端退出超时。' }
    foreach ($source in [IO.Directory]::GetFiles($Staging, '*', 'AllDirectories')) {
        $relative = $source.Substring($Staging.Length + 1)
        $destination = Join-Path $Target $relative
        if ($relative -eq 'logging.json' -and [IO.File]::Exists($destination)) { continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $backup = $null
        if ([IO.File]::Exists($destination)) {
            $backup = Join-Path $backupRoot $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            [IO.File]::Replace($source, $destination, $backup)
        } else {
            [IO.File]::Move($source, $destination)
        }
        $applied.Push(@{ Destination = $destination; Backup = $backup })
    }
    Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
    Write-UpdateLog "更新完成并已重启"
} catch {
    Write-UpdateLog "更新失败：$_"
    while ($applied.Count -gt 0) {
        $file = $applied.Pop()
        try {
            if ($file.Backup) { [IO.File]::Copy($file.Backup, $file.Destination, $true) }
            else { [IO.File]::Delete($file.Destination) }
        } catch {
            $keepBackup = $true
            Write-UpdateLog "恢复文件失败，保留更新备份：$($file.Destination)：$_"
        }
    }
    if ($updateLock -and -not $keepBackup -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and
        (Test-Path -LiteralPath (Join-Path $Target $Exe))) {
        Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
    }
} finally {
    if ($updateLock) { $updateLock.Dispose() }
    if (-not $keepBackup) { Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
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
        ConnectButton.Style = (Style)FindResource(text == "已连接" ? "ReconnectIconButton" : "ConnectPrimaryButton");
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
    private void UpdateSelection(int selected, int? total)
    {
        SelectionText.Text = total is null ? "" : selected == 0
            ? $"{total} 项" : $"已选择 {selected} 项 / 共 {total} 项";
        SelectionText.ToolTip = SelectionText.Text;
    }
    private Brush ThemeBrush(string key) => (Brush)FindResource(key);
}
