using System.Windows.Controls;
using System.Windows.Input;
using SLog = Serilog.Log;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Transfer.Core;

namespace Transfer.Server;
public partial class MainWindow : Window
{
    public ObservableCollection<ProgressRow> Files { get; } = [];
    public ObservableCollection<ProgressRow> Transfers { get; } = [];
    private readonly Dictionary<string, ProgressRow> transferRows = [];
    private readonly Dictionary<string, DateTime> lastSeen = [];
    private readonly System.Windows.Threading.DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileServer server = new();
    private readonly SemaphoreSlim adding = new(1);
    private readonly string settings;
    private bool closed;
    private bool closing;
    private readonly CancellationTokenSource lifetime = new();
    public MainWindow() : this(AppLogging.GetSettingsPath("server-files.json")) { }
    public MainWindow(string settingsPath)
    {
        settings = Path.GetFullPath(settingsPath);
        SLog.Information("准备创建Server主窗口");
        InitializeComponent(); DataContext = this;
        SLog.Information("主窗口初始化完成；配置路径 {SettingsPath}", settings);
        monitor.Tick += (_, _) =>
        {
            foreach (var pair in lastSeen)
                if (DateTime.UtcNow - pair.Value > TimeSpan.FromSeconds(12) && transferRows[pair.Key].Status == "下载中")
                {
                    transferRows[pair.Key].Status = "连接中断，可续传";
                    SLog.Warning("客户端进度心跳超时；文件 {FileId}，关联键 {TransferKey}，最后报告时间 {LastSeenUtc}", transferRows[pair.Key].File.Id, pair.Key, pair.Value);
                }
        };
        monitor.Start();
        server.FilesChanged += () => Dispatcher.BeginInvoke(RefreshSharedFiles);
        server.Log += message => Dispatcher.BeginInvoke(() => Log(message));
        server.Progress += p => Dispatcher.BeginInvoke(() =>
        {
            string key = p.FileId + p.Session;
            lastSeen[key] = DateTime.UtcNow;
            if (!transferRows.TryGetValue(key, out var row))
            {
                var file = server.Files.FirstOrDefault(f => f.Id == p.FileId);
                if (file == null) return;
                row = new() { File = file }; transferRows[key] = row; Transfers.Add(row);
            }
            row.Update(p);
            if (!server.IsRunning && (p.State == "下载中" || p.State == "校验中")) row.Status = "服务已停止，可续传";
        });
        Loaded += async (_, _) =>
        {
            try
            {
                SLog.Information("开始恢复服务端共享文件配置；路径 {SettingsPath}，存在 {Exists}", settings, File.Exists(settings));
                if (File.Exists(settings))
                {
                    var savedPaths = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(settings)) ?? [];
                    if (File.Exists(settings + ".excluded")) server.RestoreExclusions(System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(settings + ".excluded")) ?? []);
                    SLog.Information("共享文件配置读取完成；路径数量 {PathCount}", savedPaths.Length);
                    await AddPathsAsync(savedPaths);
                }
            }
            catch (Exception ex) { SLog.Error(ex, "恢复共享文件配置失败；路径 {SettingsPath}", settings); Log("恢复共享列表失败：" + ex.Message); }
        };
        Closing += async (_, e) =>
        {
            if (closed) { SLog.Debug("资源清理已完成，允许关闭窗口"); return; }
            e.Cancel = true;
            if (closing) { SLog.Debug("忽略关闭过程中的重复关闭请求"); return; }
            SLog.Information("用户请求关闭主窗口，开始清理资源");
            closing = true; IsEnabled = false;
            // 即使后续 await 同步完成，也必须先退出本次 Closing 事件，避免重入 Close。
            await Task.Yield();
            try
            {
                lifetime.Cancel(); monitor.Stop();
                await adding.WaitAsync();
                try { await server.DisposeAsync(); }
                finally { adding.Release(); }
            }
            catch (Exception ex) { SLog.Error(ex, "关闭服务时资源清理失败"); Log("关闭服务时清理失败：" + ex.Message); }
            finally { SLog.Information("关闭前资源清理结束，准备关闭主窗口"); closed = true; Close(); }
        };
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = AppLogging.LogDirectory;
            if (string.IsNullOrEmpty(directory)) throw new IOException("日志尚未初始化");
            SLog.Information("用户打开日志目录；路径 {LogDirectory}", directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { SLog.Error(ex, "打开日志目录失败"); MessageBox.Show(ex.Message, "日志目录"); }
    }
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SLog.Information("用户点击启动服务；端口输入 {PortInput}", PortBox.Text);
            if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) throw new IOException("请输入 1～65535 的端口");
            server.Start(port); StartButton.IsEnabled = false; StopButton.IsEnabled = true; PortBox.IsEnabled = false;
            SetStatus($"正在监听 0.0.0.0:{port}；本机地址：" + string.Join("、", Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)), "SuccessBrush");
        }
        catch (Exception ex) { SLog.Error(ex, "启动服务失败；端口输入 {PortInput}", PortBox.Text); SetStatus("启动服务失败：" + ex.Message, "DangerBrush"); MessageBox.Show(ex.Message, "启动失败"); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!StopButton.IsEnabled || closing) return;
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        SetStatus("正在停止服务，等待传输连接释放……", "WarningBrush");
        SLog.Information("用户点击停止服务；当前端口 {Port}", server.Port);
        try
        {
            await server.StopAsync();
            foreach (var row in Transfers.Where(r => r.Status == "下载中" || r.Status == "校验中" || r.Status == "连接中断，可续传"))
                row.Status = "服务已停止，可续传";
            SetStatus("服务已停止；共享文件保留，点击启动服务可重新监听。客户端可保留断点继续下载。", "MutedBrush");
            Log("服务已停止，监听与传输连接已释放，共享文件列表保留。");
        }
        catch (Exception ex)
        {
            SLog.Error(ex, "停止服务失败");
            SetStatus("停止服务时发生错误：" + ex.Message, "DangerBrush");
        }
        finally
        {
            if (!closing)
            {
                StartButton.IsEnabled = !server.IsRunning;
                StopButton.IsEnabled = server.IsRunning;
                PortBox.IsEnabled = !server.IsRunning;
            }
        }
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择共享文件夹" };
        if (dialog.ShowDialog() == true) await AddPathsAsync(new[] { dialog.FolderName });
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        SLog.Debug("用户打开共享文件选择对话框");
        var dialog = new OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog() == true) await AddPathsAsync(dialog.FileNames);
        else SLog.Debug("用户取消选择共享文件");
    }
    private void Files_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] list)
        {
            SLog.Information("收到拖拽共享文件；数量 {FileCount}，路径 {@SourcePaths}", list.Length, list);
            await AddPathsAsync(list);
        }
        else SLog.Warning("拖拽数据不包含文件路径");
    }
    private void SharedRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && !row.IsSelected)
        {
            SharedFilesGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }
    private async void RemoveShared_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        var selected = SharedFilesGrid.SelectedItems.Cast<ProgressRow>().ToArray();
        if (selected.Length == 0) return;
        var ids = selected.Select(r => r.File.Id).ToHashSet(StringComparer.Ordinal);
        SLog.Information("用户请求移除共享记录；文件编号 {@FileIds}", ids);
        await adding.WaitAsync();
        try
        {
            if (closing) return;
            // 先原子保存剩余共享路径，写入失败时保持界面和服务端列表不变。
            var removedDirectories = selected.Where(r => r.File.IsDirectory).Select(r => r.SourcePath + Path.DirectorySeparatorChar).ToArray();
            var remainingPaths = paths.Where(p => !selected.Any(r => r.SourcePath.Equals(p, StringComparison.OrdinalIgnoreCase)) &&
                !removedDirectories.Any(d => p.StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToArray();
            await SavePathsAsync(remainingPaths);
            foreach (var id in ids) server.RemoveFile(id);
            await File.WriteAllTextAsync(settings + ".excluded", System.Text.Json.JsonSerializer.Serialize(server.ExcludedPaths));
            paths.Clear();
            paths.UnionWith(remainingPaths);
            RefreshSharedFiles();
            Log($"已移除 {selected.Length} 条共享记录，源文件保留；客户端刷新列表后生效。");
        }
        catch (Exception ex)
        {
            SLog.Error(ex, "移除共享文件或保存共享配置失败；文件编号 {@FileIds}", ids);
            MessageBox.Show("移除共享失败：" + ex.Message, "移除共享");
        }
        finally { adding.Release(); }
    }
    private void RefreshSharedFiles()
    {
        Files.Clear();
        foreach (var shared in server.Files)
            Files.Add(new() { File = shared, SourcePath = server.GetSourcePath(shared.Id), Status = "已共享" });
    }
    private async Task SavePathsAsync(IEnumerable<string> sourcePaths)
    {
        var snapshot = sourcePaths.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings + ".tmp", System.Text.Json.JsonSerializer.Serialize(snapshot));
        File.Move(settings + ".tmp", settings, true);
        SLog.Information("共享路径配置已保存；配置路径 {SettingsPath}，路径数量 {PathCount}", settings, snapshot.Length);
    }
    private async Task AddPathsAsync(IEnumerable<string> list)
    {
        var sourcePaths = list.ToArray();
        SLog.Information("准备添加共享文件；数量 {FileCount}，路径 {@SourcePaths}", sourcePaths.Length, sourcePaths);
        await adding.WaitAsync();
        try
        {
            foreach (var path in sourcePaths)
            {
                if (lifetime.IsCancellationRequested) { SLog.Information("关闭过程中停止添加剩余共享文件"); break; }
                if (!File.Exists(path) && !Directory.Exists(path)) { SLog.Warning("跳过不存在的源文件或文件夹；源路径 {SourcePath}", path); Log("跳过不存在的文件或文件夹：" + path); continue; }
                try
                {
                    Log("计算文件校验值：" + path);
                    await Task.Run(async () =>
                    {
                        if (Directory.Exists(path)) await server.AddFolderAsync(path, lifetime.Token);
                        else await server.AddFileAsync(path, lifetime.Token);
                    });
                    paths.Add(path);
                    RefreshSharedFiles();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { SLog.Information("共享文件哈希计算因关闭而取消；源路径 {SourcePath}", path); }
                catch (Exception ex) { SLog.Error(ex, "添加共享文件失败；源路径 {SourcePath}", path); Log("添加失败：" + ex.Message); }
            }
            await SavePathsAsync(paths);
            await File.WriteAllTextAsync(settings + ".excluded", System.Text.Json.JsonSerializer.Serialize(server.ExcludedPaths));
        }
        catch (Exception ex) { SLog.Error(ex, "保存共享文件配置失败；配置路径 {SettingsPath}", settings); Log("保存共享路径失败：" + ex.Message); }
        finally { adding.Release(); }
    }
    private void Log(string text) { SLog.Debug("服务端界面消息：{UiMessage}", text); LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n"); LogBox.ScrollToEnd(); }
    private void SetStatus(string text, string brushKey = "MutedBrush")
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        var brush = (Brush)FindResource(brushKey);
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
    }
}

