using SLog = Serilog.Log;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Transfer.Core;
using Transfer.Desktop;
namespace Transfer.Client;
public partial class MainWindow : Window
{
    public ObservableCollection<ProgressRow> Files { get; } = [];
    private readonly Dictionary<ProgressRow, (CancellationTokenSource Cancellation, Task Task)> active = [];
    private readonly SemaphoreSlim fileSlots = new(3);
    private readonly string settings;
    private string connectedHost = "";
    private int connectedPort;
    private bool closed;
    private bool closing;
    public MainWindow() : this(AppLogging.GetSettingsPath("client-folder.txt")) { }
    public MainWindow(string settingsPath)
    {
        settings = settingsPath;
        SLog.Information("准备创建Client主窗口");
        InitializeComponent(); DataContext = this;
        WindowLayout.Attach(this, "transfer-client", FileGrid);
        UpdateEmptyState();
        SLog.Information("主窗口初始化完成；配置路径 {SettingsPath}", settings);
        try
        {
            if (File.Exists(settings)) FolderBox.Text = File.ReadAllText(settings);
            SLog.Information("下载目录设置已恢复；配置存在 {Exists}，下载目录 {DownloadFolder}", File.Exists(settings), FolderBox.Text);
        }
        catch (Exception ex) { SLog.Warning(ex, "读取下载目录设置失败；配置路径 {SettingsPath}", settings); }
        Closing += async (_, e) =>
        {
            if (closed) { SLog.Debug("资源清理已完成，允许关闭窗口"); return; }
            e.Cancel = true;
            if (closing) { SLog.Debug("忽略关闭过程中的重复关闭请求"); return; }
            SLog.Information("用户请求关闭主窗口，开始清理资源");
            closing = true; IsEnabled = false;
            // 没有下载任务时 WhenAll 会同步完成，先让原始关闭事件返回。
            await Task.Yield();
            try
            {
                var downloads = active.Values.ToArray();
                SLog.Information("关闭客户端，取消活动下载并等待断点保存；任务数量 {DownloadCount}", downloads.Length);
                foreach (var item in downloads) item.Cancellation.Cancel();
                await Task.WhenAll(downloads.Select(v => v.Task));
            }
            catch (Exception ex) { SLog.Error(ex, "关闭客户端时等待下载结束失败"); SetStatus("关闭时保存下载进度失败：" + ex.Message, "DangerBrush"); }
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
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SLog.Information("用户点击连接或刷新；地址输入 {HostInput}，端口输入 {PortInput}", HostBox.Text, PortBox.Text);
        if (active.Count > 0) { SLog.Warning("活动下载存在，拒绝刷新；任务数 {ActiveDownloads}", active.Count); MessageBox.Show("请先暂停所有下载，再刷新列表"); return; }
        ConnectButton.IsEnabled = false;
        SetStatus("正在连接服务端…", "WarningBrush");
        try
        {
            if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) throw new IOException("端口无效");
            var list = await Downloader.ListAsync(HostBox.Text.Trim(), port);
            connectedHost = HostBox.Text.Trim(); connectedPort = port;
            Files.Clear(); foreach (var file in list) Files.Add(new() { File = file });
            UpdateEmptyState();
            SetStatus($"已连接 {connectedHost}:{port}，共 {list.Length} 个文件", "SuccessBrush");
        }
        catch (Exception ex) { SLog.Error(ex, "客户端连接或刷新失败；地址 {Host}，端口输入 {PortInput}", HostBox.Text, PortBox.Text); SetStatus("连接失败：" + ex.Message, "DangerBrush"); UpdateEmptyState(); }
        finally { ConnectButton.IsEnabled = true; }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        SLog.Debug("用户打开下载目录选择对话框");
        var dialog = new OpenFolderDialog { Title = "选择下载保存文件夹" };
        if (dialog.ShowDialog() != true) { SLog.Debug("用户取消选择下载目录"); return; }
        FolderBox.Text = dialog.FolderName;
        SLog.Information("用户选择下载目录；目录 {DownloadFolder}", dialog.FolderName);
        try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.WriteAllText(settings, dialog.FolderName); SLog.Information("下载目录配置已保存；配置路径 {SettingsPath}", settings); }
        catch (Exception ex) { SLog.Error(ex, "保存下载目录配置失败；配置路径 {SettingsPath}，目录 {DownloadFolder}", settings, dialog.FolderName); MessageBox.Show("保存目录设置失败：" + ex.Message); }
    }
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        SLog.Information("用户点击下载或继续；所选数 {SelectedCount}，目录 {DownloadFolder}，并发输入 {ParallelismInput}", FileGrid.SelectedItems.Count, FolderBox.Text, ThreadsBox.Text);
        if (!ConnectButton.IsEnabled) { SLog.Warning("下载参数校验未通过：{Reason}", "正在连接，请稍候"); MessageBox.Show("正在连接，请稍候"); return; }
        if (connectedPort == 0) { SLog.Warning("下载参数校验未通过：{Reason}", "请先连接服务端"); MessageBox.Show("请先连接服务端"); return; }
        if (string.IsNullOrWhiteSpace(FolderBox.Text)) { SLog.Warning("下载参数校验未通过：{Reason}", "请选择下载目录"); MessageBox.Show("请选择下载目录"); return; }
        if (!int.TryParse(ThreadsBox.Text, out int threads) || threads is < 1 or > 16) { SLog.Warning("下载参数校验未通过：{Reason}", "并发数请输入 1～16"); MessageBox.Show("并发数请输入 1～16"); return; }
        var selected = FileGrid.SelectedItems.Cast<ProgressRow>().ToArray();
        if (selected.Length == 0) { SLog.Warning("下载参数校验未通过：{Reason}", "请先选择文件，可按 Ctrl 多选"); MessageBox.Show("请先选择文件，可按 Ctrl 多选"); return; }
        var downloads = selected.Where(r => !r.File.IsDirectory).Concat(
            selected.Where(r => r.File.IsDirectory).SelectMany(directory => Files.Where(r => !r.File.IsDirectory &&
                (r.File.RelativePath ?? "").StartsWith((directory.File.RelativePath ?? directory.Name) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))).Distinct().ToArray();
        try
        {
        foreach (var directory in selected.Where(r => r.File.IsDirectory))
        {
            Directory.CreateDirectory(Downloader.GetDestinationPath(directory.File, FolderBox.Text));
            foreach (var child in Files.Where(r => r.File.IsDirectory && (r.File.RelativePath ?? "").StartsWith((directory.File.RelativePath ?? directory.Name) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                Directory.CreateDirectory(Downloader.GetDestinationPath(child.File, FolderBox.Text));
        }
        }
        catch (Exception ex)
        {
            SLog.Error(ex, "创建共享文件夹的下载目录失败"); SetStatus(ex.Message, "DangerBrush"); return;
        }
        foreach (var row in downloads)
        {
            if (active.ContainsKey(row) || row.Status == "已完成") { SLog.Debug("跳过已运行或已完成的文件；文件编号 {FileId}，状态 {State}", row.File.Id, row.Status); continue; }
            var cancellation = new CancellationTokenSource();
            var task = RunAsync(row, FolderBox.Text, threads, cancellation);
            active.Add(row, (cancellation, task));
        }
    }
    private async Task RunAsync(ProgressRow row, string folder, int threads, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        bool entered = false;
        try
        {
            SLog.Information("文件下载任务已排队；文件 {FileName}，编号 {FileId}，目录 {DownloadFolder}，并发数 {Parallelism}", row.Name, row.File.Id, folder, threads);
            row.Status = "排队中";
            await fileSlots.WaitAsync(cancellation.Token); entered = true;
            SLog.Information("文件下载任务取得运行许可；文件编号 {FileId}", row.File.Id);
            var progress = new Progress<TransferProgress>(row.Update);
            await Task.Run(() => new Downloader().DownloadAsync(connectedHost, connectedPort, row.File, folder, threads, progress, cancellation.Token));
            row.Status = "已完成";
        }
        catch (OperationCanceledException ex)
        {
            row.Status = cancellation.IsCancellationRequested ? "已暂停" : "已中断";
            if (cancellation.IsCancellationRequested) SLog.Information("客户端文件任务已暂停；文件编号 {FileId}", row.File.Id);
            else SLog.Error(ex, "客户端文件任务发生超时或内部取消；文件编号 {FileId}", row.File.Id);
        }
        catch (Exception ex) { SLog.Error(ex, "客户端文件下载失败；文件 {FileName}，编号 {FileId}，目录 {DownloadFolder}", row.Name, row.File.Id, folder); row.Status = "已中断"; SetStatus($"{row.Name}：{ex.Message}", "DangerBrush"); }
        finally
        {
            if (entered) fileSlots.Release();
            active.Remove(row); cancellation.Dispose();
            SLog.Information("客户端文件任务资源已释放；文件编号 {FileId}，最终状态 {State}，剩余任务 {RemainingTasks}", row.File.Id, row.Status, active.Count);
        }
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        SLog.Information("用户请求暂停所选文件；数量 {SelectedCount}", FileGrid.SelectedItems.Count);
        foreach (var row in FileGrid.SelectedItems.Cast<ProgressRow>())
            if (active.TryGetValue(row, out var item))
            {
                SLog.Information("发送文件暂停信号；文件 {FileName}，编号 {FileId}", row.Name, row.File.Id);
                item.Cancellation.Cancel();
            }
    }
    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        SLog.Information("用户请求暂停所有下载；活动任务数量 {ActiveDownloads}", active.Count);
        foreach (var item in active.Values) item.Cancellation.Cancel();
    }
    private void SetStatus(string text, string brushKey = "MutedBrush")
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        var brush = (Brush)FindResource(brushKey);
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
    }
    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = connectedPort == 0 ? "连接服务端后选择要下载的文件" : "当前服务端没有可下载文件";
        EmptyAction.Content = connectedPort == 0 ? "连接 / 刷新" : "重新连接 / 刷新";
    }
}

