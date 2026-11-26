extern alias RemoteServer;
extern alias RemoteClient;
using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Transfer.Core.Remote;
using ClientWindow = RemoteClient::RemoteFiles.Client.MainWindow;
using ServerWindow = RemoteServer::RemoteFiles.Server.MainWindow;
using ClientBrowser = RemoteClient::RemoteFiles.Desktop.RemoteBrowser;
using TextViewer = RemoteClient::RemoteFiles.Desktop.TextViewer;
using ImageViewer = RemoteClient::RemoteFiles.Desktop.ImageViewer;
using AnnotationContext = RemoteClient::RemoteFiles.Desktop.AnnotationContext;
using ImageThumbItem = RemoteClient::RemoteFiles.Desktop.ImageThumbItem;
using ProcessViewer = RemoteClient::RemoteFiles.Desktop.ProcessViewer;

internal static partial class RemoteWindowChecks
{
    public static async Task CheckNavigationBusyAsync(string temp)
    {
        await CheckSlowTreeResponsivenessAsync();
        string root = Path.Combine(temp, "navigation-busy-root");
        Directory.CreateDirectory(Path.Combine(root, "机台", "日期"));
        foreach (int number in Enumerable.Range(1, 45)) Directory.CreateDirectory(Path.Combine(root, "机台", "日期", number.ToString()));
        await using var service = new RemoteFileService(root);
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        var browser = new ClientBrowser();
        var host = new Window { Content = browser, Width = 1280, Height = 720 };
        Prepare(host); host.Show();
        bool keepBackgroundQueued = true;
        Task? navigation = null;
        try
        {
            await browser.ConnectAsync(endpoint);
            // 展开树节点不能进入全局操作锁，否则紧接着的目录点击会被吞掉。
            var tree = (TreeView)browser.FindName("FolderTree");
            var rootNode = (TreeViewItem)tree.Items[0];
            var machineNode = rootNode.Items.OfType<TreeViewItem>().Single();
            machineNode.IsExpanded = true;
            if (browser.IsBusy || !((DataGrid)browser.FindName("FileGrid")).IsEnabled || !tree.IsEnabled)
                throw new Exception("目录树展开只能加载当前节点，不能锁住整个资源管理器");
            await WaitAsync(() => machineNode.Items.OfType<TreeViewItem>().Any());
            var children = machineNode.Items.Cast<object>().ToArray();
            machineNode.IsExpanded = false;
            machineNode.IsExpanded = true;
            await FlushAsync(host);
            if (!children.SequenceEqual(machineNode.Items.Cast<object>()))
                throw new Exception("重复展开已加载节点不应重建子项并丢失选中或展开状态");
            var dispatcher = browser.Dispatcher;
            Action? occupyBackground = null;
            occupyBackground = () =>
            {
                if (keepBackgroundQueued) dispatcher.BeginInvoke(occupyBackground!, System.Windows.Threading.DispatcherPriority.Background);
            };
            _ = dispatcher.BeginInvoke(occupyBackground, System.Windows.Threading.DispatcherPriority.Background);
            var execute = typeof(ClientBrowser).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [typeof(Func<Task>)], null)!;
            navigation = (Task)execute.Invoke(browser, [new Func<Task>(() => browser.OpenAsync(new("日期", "机台/日期", true, 0, default, default)))])!;
            await Task.WhenAny(navigation, Task.Delay(1500));
            if (!navigation.IsCompleted || browser.IsBusy || !((DataGrid)browser.FindName("FileGrid")).IsEnabled
                || !((TreeView)browser.FindName("FolderTree")).IsEnabled)
                throw new Exception("列表显示后的导航不能等待 ContextIdle 才解除操作锁");
            await navigation;
            keepBackgroundQueued = false;
            var banner = (Border)browser.FindName("BusyIndicator");
            if (banner.Visibility != Visibility.Collapsed) throw new Exception("导航完成后不能残留加载提示");
            var grid = (DataGrid)browser.FindName("FileGrid");
            if (grid.Items.Count != 45) throw new Exception("目录列表内容错误");
            await FlushAsync(host);
            var scroll = FindScrollViewer(grid)!;
            scroll.ScrollToBottom();
            await WaitAsync(() => scroll.VerticalOffset > 0);
            var folder = (RemoteEntry)grid.Items[0];
            await (Task)execute.Invoke(browser, [new Func<Task>(() => browser.OpenAsync(folder))])!;
            await FlushAsync(host);
            typeof(ClientBrowser).GetMethod("Parent_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(browser, [browser, new RoutedEventArgs()]);
            await WaitAsync(() => !browser.IsBusy && grid.SelectedItem is RemoteEntry returned && returned.Path == folder.Path);
            await (Task)execute.Invoke(browser, [new Func<Task>(() => browser.OpenAsync(new("机台", "机台", true, 0, default, default)))])!;
            await (Task)execute.Invoke(browser, [new Func<Task>(() => browser.OpenAsync(new("日期", "机台/日期", true, 0, default, default)))])!;
            await FlushAsync(host);
            if (scroll.VerticalOffset != 0) throw new Exception("新目录应恢复顶部，旧导航的滚动任务不能覆盖新目录");
            var show = typeof(ClientBrowser).GetMethod("ShowBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var display = typeof(ClientBrowser).GetMethod("DisplayBusyIndicator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var hide = typeof(ClientBrowser).GetMethod("HideBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowOperation = (Task)execute.Invoke(browser, [new Func<Task>(() => release.Task)])!;
            try
            {
                if (((TextBlock)browser.FindName("StatusText")).Text != "正在处理…")
                    throw new Exception("操作开始必须立即反馈状态，不能只依靠延迟提示");
                await Task.Delay(220);
                if (!banner.IsVisible) throw new Exception("耗时操作应在 150ms 后显示顶部加载提示");
            }
            finally { release.TrySetResult(); await slowOperation; }
            if (banner.IsVisible || browser.IsBusy || ((TextBlock)browser.FindName("StatusText")).Text != "就绪")
                throw new Exception("操作结束必须恢复就绪并移除加载提示");
            var executeCancellable = typeof(ClientBrowser).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [typeof(Func<CancellationToken, Task>), typeof(bool)], null)!;
            var cancellable = (Task)executeCancellable.Invoke(browser,
                [new Func<CancellationToken, Task>(token => Task.Delay(Timeout.Infinite, token)), true])!;
            try
            {
                var cancel = (Button)browser.FindName("CancelButton");
                await WaitAsync(() => cancel.IsVisible);
                if (!((Border)browser.FindName("PathBar")).IsAncestorOf(cancel))
                    throw new Exception("取消入口应随等待提示显示在路径栏内");
                typeof(ClientBrowser).GetMethod("SetBusyProgress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(browser, [65d, "正在下载 65%"]);
                await FlushAsync(host);
                var progress = (ProgressBar)browser.FindName("BusyProgress");
                var indicator = (FrameworkElement)progress.Template.FindName("PART_Indicator", progress);
                if (progress.IsIndeterminate || progress.Value != 65 || indicator.ActualWidth <= 0 || indicator.ActualWidth >= progress.ActualWidth)
                    throw new Exception("路径栏进度线必须支持实际百分比显示");
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await cancellable;
                if (browser.IsBusy || banner.IsVisible || ((TextBlock)browser.FindName("StatusText")).Text != "已取消")
                    throw new Exception("路径栏取消操作后应恢复交互并保留取消状态");
            }
            finally
            {
                if (!cancellable.IsCompleted) ((Button)browser.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await cancellable;
            }
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(960, 540) })
            {
                host.Width = size.Width; host.Height = size.Height;
                show.Invoke(browser, ["正在读取目录…"]); display.Invoke(browser, null);
                await FlushAsync(host);
                var pathBar = browser.FindName("PathBar") as Border ?? throw new Exception("加载反馈应集成到路径栏");
                var pathOrigin = pathBar.TranslatePoint(new Point(), browser);
                var point = banner.TranslatePoint(new Point(), browser);
                var line = (System.Windows.Controls.ProgressBar)browser.FindName("BusyProgress");
                var lineOrigin = line.TranslatePoint(new Point(), browser);
                var text = (TextBlock)browser.FindName("BusyText");
                if (point.Y < pathOrigin.Y || point.Y + banner.ActualHeight > pathOrigin.Y + pathBar.ActualHeight
                    || point.X < pathOrigin.X || point.X + banner.ActualWidth > pathOrigin.X + pathBar.ActualWidth
                    || line.Height != 2 || lineOrigin.Y + line.ActualHeight > pathOrigin.Y + pathBar.ActualHeight
                    || line.ActualWidth < pathBar.ActualWidth - 8 || text.FontSize > 12 || text.FontWeight != FontWeights.Normal
                    || banner.BorderThickness != new Thickness(0))
                    throw new Exception("加载文字和 2px 进度线应完整位于路径栏内，不能覆盖表头或使用粗体边框");
                if (size.Width == 1280 && Environment.GetEnvironmentVariable("FILETRANSFER_BUSY_PREVIEW") is { Length: > 0 } preview)
                {
                    var rendered = new RenderTargetBitmap((int)Math.Ceiling(browser.ActualWidth), (int)Math.Ceiling(browser.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(browser);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(rendered, new Int32Rect(0, 0, rendered.PixelWidth, Math.Min(220, rendered.PixelHeight)))));
                    using var output = File.Create(preview); encoder.Save(output);
                }
                hide.Invoke(browser, null);
            }
            Console.WriteLine("通过：树节点独立加载及复用、操作立即反馈、耗时提示及时出现并自动结束、导航及时解锁及投屏尺寸顶部提示可见");
        }
        finally
        {
            keepBackgroundQueued = false;
            if (navigation is not null) await navigation;
            host.Close();
        }
    }

    private static async Task CheckSlowTreeResponsivenessAsync()
    {
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int slowRequests = 0;
        var connections = new System.Collections.Concurrent.ConcurrentBag<Task>();
        async Task RespondAsync(System.Net.Sockets.TcpClient socket)
        {
            using (socket)
            {
                var stream = socket.GetStream();
                var request = await Transfer.Core.Wire.ReadAsync<RemoteRequest>(stream, stop.Token);
                if (request.Path == "慢目录")
                {
                    Interlocked.Increment(ref slowRequests);
                    started.TrySetResult();
                    await release.Task.WaitAsync(stop.Token);
                }
                string[] names = request.Path.Length == 0 ? ["慢目录", "快目录"] : ["子目录"];
                var rows = names.Select(name => new RemoteEntry(name,
                    request.Path.Length == 0 ? name : request.Path + "/" + name, true, 0, default, default)).ToArray();
                await Transfer.Core.Wire.WriteAsync(stream, new RemoteResponse(true, Entries: rows, RootName: "测试目录", Total: rows.Length), stop.Token);
            }
        }
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    connections.Add(RespondAsync(await listener.AcceptTcpClientAsync(stop.Token)));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var browser = new ClientBrowser();
        var host = new Window { Content = browser, Width = 1280, Height = 720 };
        Prepare(host); host.Show();
        try
        {
            await browser.ConnectAsync(new("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port));
            var tree = (TreeView)browser.FindName("FolderTree");
            var nodes = ((TreeViewItem)tree.Items[0]).Items.OfType<TreeViewItem>().ToArray();
            var slow = nodes.Single(node => ((RemoteEntry)node.Tag).Path == "慢目录");
            slow.IsExpanded = true;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            slow.IsExpanded = false; slow.IsExpanded = true;
            if (browser.IsBusy || !tree.IsEnabled) throw new Exception("慢目录展开不能阻止其他目录操作");
            nodes.Single(node => ((RemoteEntry)node.Tag).Path == "快目录").IsSelected = true;
            var grid = (DataGrid)browser.FindName("FileGrid");
            await WaitAsync(() => !browser.IsBusy && grid.Items.OfType<RemoteEntry>().Any(row => row.Path == "快目录/子目录"));
            if (release.Task.IsCompleted || slowRequests != 1) throw new Exception("慢节点请求期间应能打开其他目录，且不能重复发起展开请求");
            release.TrySetResult();
            await WaitAsync(() => slow.Items.OfType<TreeViewItem>().Any());
            if (!grid.Items.OfType<RemoteEntry>().Any(row => row.Path == "快目录/子目录"))
                throw new Exception("后台树节点返回不能覆盖当前目录列表");
            Console.WriteLine("通过：慢节点请求未返回时仍可打开其他目录，重复展开请求合并，晚到结果不覆盖当前列表");
        }
        finally
        {
            release.TrySetResult();
            host.Close();
            stop.Cancel();
            await serving;
            listener.Stop();
            try { await Task.WhenAll(connections); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

}
