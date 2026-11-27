using System.Reflection;
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
    private readonly EndpointSettingsService endpointSettings;
    private bool connecting;
    private CancellationTokenSource? updateCheck;
    private string connectedHost = "";
    private int connectedPort;
    private string[] recentEndpoints = [];
    private readonly ClientUpdateService updateService = new();
    private RemoteUpdateInfo? pendingUpdate;
    private LogViewer? logViewer;
    private AnnotationDatabaseWindow? annotationDatabaseWindow;
    // 连接期间每 30 分钟静默复查一次，发现新版本只显示蓝色提示，不打断使用。
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    public MainWindow() : this(AppLogging.GetSettingsPath("remote-client.json")) { }
    public MainWindow(string settingsPath)
    {
        endpointSettings = new(settingsPath); InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
        Title = $"远程文件管理 · 客户端 v{version.ToString(3)}";
        UpdateThemeButton();
        if (Browser.FindName("FileGrid") is DataGrid fileGrid) WindowLayout.Attach(this, "remote-client", fileGrid);
        Browser.StatusChanged += (text, key, highlight) => SetStatus(text, ThemeBrush(key), highlight);
        Browser.ProgressChanged += UpdateBrowserProgress;
        Browser.SelectionCountChanged += UpdateSelection;
        SetConnectionState("未连接", ThemeBrush("MutedBrush"));
        SetStatus("就绪", ThemeBrush("MutedBrush"));
        var (endpoint, recent) = endpointSettings.Load();
        recentEndpoints = recent;
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
        EndpointBox.ItemsSource = recentEndpoints.Concat(EndpointSettingsService.PresetEndpoints).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        EndpointBox.Text = endpoint;
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
        if (!EndpointSettingsService.TryParseEndpoint(EndpointBox.Text, out string host, out int port))
        {
            SetStatus("请输入地址和端口，端口范围为 1～65535，例如 10.100.40.181:9501。", ThemeBrush("WarningBrush"));
            EndpointBox.Focus();
            return;
        }
        connecting = true; ConnectButton.IsEnabled = false; EndpointBox.IsEnabled = false; Browser.IsEnabled = false;
        annotationDatabaseWindow?.Close();
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
            string endpoint = EndpointSettingsService.FormatEndpoint(host, port);
            recentEndpoints = recentEndpoints.Prepend(endpoint).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            SetEndpointOptions(endpoint);
            bool settingsSaved = true;
            try { await endpointSettings.SaveAsync(host, port, recentEndpoints); }
            catch (Exception ex) { settingsSaved = false; Log.Warning(ex, "保存客户端配置失败；路径 {SettingsPath}", endpointSettings.Path); }
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
            if (!ClientUpdateService.IsNewer(latest.Version, current))
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
            if (!ClientUpdateService.IsNewer(info.Version, current))
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
            await updateService.ApplyUpdateAsync(this, new RemoteFileClient(connectedHost, connectedPort), info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "客户端更新失败");
            Dialog.Notify(this, "更新失败：" + ex.Message, "检查更新", DialogTone.Warning);
        }
    }
    private void AnnotationDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (connecting || connectedPort == 0)
        {
            SetStatus("请先连接服务端。", ThemeBrush("WarningBrush"));
            return;
        }
        if (annotationDatabaseWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new AnnotationDatabaseWindow(new RemoteFileClient(connectedHost, connectedPort)) { Owner = this, Icon = Icon };
        annotationDatabaseWindow = window;
        window.Closed += (_, _) => annotationDatabaseWindow = null;
        window.Show();
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (logViewer is { IsVisible: true })
            {
                if (logViewer.WindowState == WindowState.Minimized) logViewer.WindowState = WindowState.Normal;
                logViewer.Activate();
                return;
            }
            var window = new LogViewer(AppLogging.LogDirectory) { Owner = this, Icon = Icon };
            logViewer = window;
            window.Closed += (_, _) => { if (ReferenceEquals(logViewer, window)) logViewer = null; };
            window.Show();
            SetStatus("已打开日志查看器", ThemeBrush("MutedBrush"));
        }
        catch (Exception ex)
        {
            logViewer = null;
            Log.Error(ex, "打开日志查看器失败；目录 {LogDirectory}", AppLogging.LogDirectory);
            SetStatus("打开日志查看器失败：" + ex.Message, ThemeBrush("DangerBrush"));
        }
    }

    private void SetConnectionState(string text, Brush dot)
    {
        ConnectionText.Text = text;
        ConnectionDot.Fill = dot;
        ConnectButton.Style = (Style)FindResource(text == "已连接" ? "ReconnectIconButton" : "ConnectPrimaryButton");
    }
    private void SetStatus(string text, Brush dot, Range? highlight = null)
    {
        RemoteBrowser.ApplyStatusText(StatusText, text, highlight);
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
