using Transfer.Core;
using System.Text.Json;
using System.Windows.Input;
using Serilog;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Transfer.WindowTests;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string logs = Path.Combine(Path.GetTempPath(), "FileTransfer-window-logs-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("FILETRANSFER_LOG_DIRECTORY", logs);
        Environment.SetEnvironmentVariable("FILETRANSFER_DISABLE_LAYOUT", "1");
        Application app = args.Contains("client") ? new Transfer.Client.App() : new Transfer.Server.App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int result = 0;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception); e.Handled = true; result = 1; app.Shutdown();
        };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await CheckAsync(new Transfer.Server.MainWindow(Path.Combine(logs, "server-files.json")), "服务端未启动时关闭", false, false);
                await CheckAsync(new Transfer.Server.MainWindow(Path.Combine(logs, "server-files.json")), "服务端未启动时连续关闭", true, false);
                await CheckAsync(new Transfer.Server.MainWindow(Path.Combine(logs, "server-files.json")), "服务端监听后连续关闭", true, true);
                await CheckAsync(new Transfer.Client.MainWindow(), "客户端无下载任务时关闭", false, false);
                await CheckAsync(new Transfer.Client.MainWindow(), "客户端无下载任务时连续关闭", true, false);
                Console.WriteLine("全部 5 项 WPF 窗口关闭回归检查通过。");
                await CheckRemovalAsync(logs);
                await RemoteWindowChecks.RunAsync(logs);
                await FolderWindowChecks.RunAsync(logs);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
        Log.CloseAndFlush();
        string logText = string.Join("\n", Directory.GetFiles(logs, "app-*.log").Select(File.ReadAllText));
        if (!logText.Contains("应用程序启动") || !logText.Contains("应用程序退出") || !logText.Contains("资源清理已完成"))
        {
            Console.Error.WriteLine("应用生命周期或关闭过程日志缺失"); result = 1;
        }
        else Console.WriteLine("通过：真实应用启动、窗口关闭和退出日志已落盘");
        if (result != 0)
            foreach (var errorLog in Directory.GetFiles(logs, "error-*.log")) Console.Error.WriteLine(File.ReadAllText(errorLog));
        Directory.Delete(logs, true);
        return result;
    }
    private static async Task CheckRemovalAsync(string root)
    {
        string first = Path.Combine(root, "移除测试一.txt"), second = Path.Combine(root, "保留测试二.txt");
        await File.WriteAllTextAsync(first, "移除共享但保留源文件");
        await File.WriteAllTextAsync(second, "保留共享");
        string settings = Path.Combine(root, "remove-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new[] { first, second }));
        var window = new Transfer.Server.MainWindow(settings) { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        window.Show();
        await WaitForAsync(() => window.Files.Count == 2);
        // 等待 Loaded 中恢复配置的异步操作结束。
        await WaitForAsync(() => !File.Exists(settings + ".tmp"));
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        ((TextBox)window.FindName("PortBox")).Text = port.ToString();
        ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var grid = (DataGrid)window.FindName("SharedFilesGrid");
        grid.UpdateLayout();
        var selected = window.Files.Single(f => f.SourcePath == first);
        var retained = window.Files.Single(f => f.SourcePath == second);
        grid.SelectedItem = retained;
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(selected);
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
        if (!ReferenceEquals(grid.SelectedItem, selected)) throw new Exception("右键未选择点击的文件行");
        ((MenuItem)grid.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitForAsync(() => window.Files.Count == 1);
        var listing = await Downloader.ListAsync("127.0.0.1", port);
        if (listing.Length != 1 || listing[0].Id != retained.File.Id || !File.Exists(first) || !File.Exists(second))
            throw new Exception("右键移除后 TCP 列表或源文件状态不正确");
        var saved = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(settings))!;
        if (saved.Length != 1 || saved[0] != second) throw new Exception("移除的共享路径仍被保存在配置中");
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var reopened = new Transfer.Server.MainWindow(settings) { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        reopened.Show();
        await WaitForAsync(() => reopened.Files.Count == 1);
        if (reopened.Files[0].SourcePath != second) throw new Exception("重启后移除的路径重新出现");
        var reopenedClosed = new TaskCompletionSource();
        reopened.Closed += (_, _) => reopenedClosed.TrySetResult();
        reopened.Close(); await reopenedClosed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("通过：右键选中、菜单移除、客户端 TCP 刷新、配置持久化、重开窗口恢复且源文件保留");
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task CheckAsync(Window window, string name, bool repeat, bool start)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Left = -32000; window.Top = -32000;
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (start)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            ((TextBox)window.FindName("PortBox")).Text = port.ToString();
            var button = (Button)window.FindName("StartButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (button.IsEnabled) throw new Exception("服务端监听未启动");
            var stopButton = (Button)window.FindName("StopButton");
            if (!stopButton.IsEnabled) throw new Exception("服务启动后停止按钮未启用");
            stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!button.IsEnabled) await Task.Delay(20, timeout.Token);
            if (stopButton.IsEnabled || !((TextBox)window.FindName("PortBox")).IsEnabled)
                throw new Exception("停止后的按钮或端口状态错误");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (button.IsEnabled || !stopButton.IsEnabled) throw new Exception("服务停止后无法重新启动");
            Console.WriteLine("通过：停止服务、重复点击与同端口重新启动按钮状态");
        }
        window.Close();
        if (repeat) { window.Close(); window.Close(); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("通过：" + name);
    }
}
