extern alias RemoteClient;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using Transfer.Core.Remote;
using Browser = RemoteClient::RemoteFiles.Desktop.RemoteBrowser;
using Navigation = RemoteClient::RemoteFiles.Desktop.DirectoryNavigationState;
using IDirectoryClient = RemoteClient::RemoteFiles.Desktop.IDirectoryClient;

internal static partial class RemoteWindowChecks
{
    private static async Task CheckNavigationContinuationsAsync(string temp)
    {
        string root = Path.Combine(temp, "continuations");
        Directory.CreateDirectory(Path.Combine(root, "A")); Directory.CreateDirectory(Path.Combine(root, "B"));
        await using var service = new RemoteFileService(root); service.Start(0);
        var browser = new Browser(); var host = new Window { Content = browser, Width = 1000, Height = 600 };
        Prepare(host); host.Show();
        try
        {
            await browser.ConnectAsync(new("127.0.0.1", service.Port));
            var state = (Navigation)typeof(Browser).GetField("navigation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(browser)!;
            var grid = (DataGrid)browser.FindName("FileGrid");
            foreach (bool sort in new[] { true, false })
            {
                var started = new TaskCompletionSource(); var release = new TaskCompletionSource<RemoteResponse>();
                state.SetClient(new DirectoryStub((request, _) =>
                {
                    RemoteEntry Row(string path) => new(path, path, true, 0, default, default);
                    if (request.Skip > 0) { started.TrySetResult(); return release.Task; }
                    if (request.Path == "B") return Task.FromResult(new RemoteResponse(true, Entries: [Row("B/only")], Total: 1));
                    return Task.FromResult(new RemoteResponse(true, Entries: Enumerable.Range(1, 500).Select(i => Row("A/" + i)).ToArray(), Total: 501));
                }));
                await browser.OpenAsync(new("A", "A", true, 0, default, default));
                Task? select = null;
                if (sort) typeof(Browser).GetMethod("Grid_Sorting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(browser, [grid, new DataGridSortingEventArgs(grid.Columns[2])]);
                else select = (Task)typeof(Browser).GetMethod("SelectAllAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(browser, null)!;
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await browser.OpenAsync(new("B", "B", true, 0, default, default));
                release.SetResult(new(true, Entries: [new("501", "A/501", true, 0, default, default)], Total: 501));
                if (select is not null) await select;
                await FlushAsync(host);
                if (grid.Items.Count != 1 || ((RemoteEntry)grid.Items[0]).Path != "B/only") throw new Exception("迟到分页不能混入新目录");
                if (sort && (grid.Columns[0].SortDirection != ListSortDirection.Ascending || grid.Columns[2].SortDirection is not null))
                    throw new Exception("旧目录的排序完成不能更改新目录表头");
                if (!sort && grid.SelectedItems.Count != 0) throw new Exception("旧目录的全选不能选择新目录条目");
            }
            var tree = (TreeView)browser.FindName("FolderTree");
            var node = ((TreeViewItem)tree.Items[0]).Items.OfType<TreeViewItem>().Single(item => ((RemoteEntry)item.Tag).Path == "A");
            node.IsExpanded = false; node.Items.Clear(); node.Items.Add("加载中……");
            int treeReads = 0;
            var oldRead = new TaskCompletionSource<RemoteResponse>();
            state.SetClient(new DirectoryStub((_, _) => ++treeReads == 1 ? oldRead.Task : Task.FromResult(new RemoteResponse(true,
                Entries: [new("child", "A/child", true, 0, default, default)]))));
            node.IsExpanded = true;
            state.InvalidateCache();
            oldRead.SetResult(new(true, Entries: []));
            await WaitAsync(() => node.Items.Count != 1 || node.Items[0] is not string text || text != "正在读取目录…");
            if (node.Items.Count != 1 || node.Items[0] is not string retry || !retry.Contains("更新"))
                throw new Exception("失效树请求应保留可重试占位，而不能清空子项或显示读取故障");
            node.IsExpanded = false; node.IsExpanded = true;
            await WaitAsync(() => node.Items.OfType<TreeViewItem>().Any());
            if (treeReads != 2) throw new Exception("失效树节点应能重新展开读取");
            var finishing = new TaskCompletionSource<RemoteResponse>(); var finishingStarted = new TaskCompletionSource();
            state.SetClient(new DirectoryStub((request, _) =>
            {
                if (request.Take > 0) return Task.FromResult(new RemoteResponse(true, Entries: [new("one", "A/one", true, 0, default, default)], Total: 2));
                finishingStarted.TrySetResult(); return finishing.Task;
            }));
            var navigation = browser.OpenAsync(new("A", "A", true, 0, default, default));
            await finishingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            browser.Disconnect(); finishing.SetResult(new(true, Entries: []));
            await navigation;
            if (((TreeView)browser.FindName("FolderTree")).Items.Count != 0 || ((TextBlock)browser.FindName("StatusText")).Text != "尚未连接")
                throw new Exception("断开后的导航收尾不能恢复树节点或覆盖断开状态");
            Console.WriteLine("通过：分页期间切目录后，旧排序和全选不会修改新列表");
        }
        finally { host.Close(); }
    }

    private static async Task CheckConnectionHandshakeAsync(string temp)
    {
        string root = Path.Combine(temp, "handshake"); Directory.CreateDirectory(root);
        await using var service = new RemoteFileService(root); service.Start(0);
        foreach (bool disconnect in new[] { false, true })
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            var started = new TaskCompletionSource(); var release = new TaskCompletionSource();
            var accepted = listener.AcceptTcpClientAsync();
            var responder = Task.Run(async () =>
            {
                using var socket = await accepted;
                var stream = socket.GetStream(); await Transfer.Core.Wire.ReadAsync<RemoteRequest>(stream, CancellationToken.None);
                started.TrySetResult(); await release.Task;
                await Transfer.Core.Wire.WriteAsync(stream, new RemoteResponse(true, Protocol: 999), CancellationToken.None);
            });
            var browser = new Browser(); var host = new Window { Content = browser, Width = 1000, Height = 600 };
            Prepare(host); host.Show();
            var connecting = browser.ConnectAsync(new("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (disconnect) browser.Disconnect();
                else await browser.ConnectAsync(new("127.0.0.1", service.Port));
                var grid = (DataGrid)browser.FindName("FileGrid"); var items = grid.ItemsSource;
                release.TrySetResult(); await connecting;
                if (browser.ProtocolWarning is not null || !ReferenceEquals(items, grid.ItemsSource))
                    throw new Exception("旧握手不能覆盖新连接的协议状态或再次导航");
            }
            finally
            {
                release.TrySetResult(); await responder; listener.Stop(); host.Close();
            }
        }
        Console.WriteLine("通过：重连和断开后旧握手返回均不覆盖当前状态");
    }

    private sealed class DirectoryStub(Func<RemoteRequest, CancellationToken, Task<RemoteResponse>> call) : IDirectoryClient
    {
        public Task<RemoteResponse> ListAsync(RemoteRequest request, CancellationToken ct) => call(request, ct);
    }
}
