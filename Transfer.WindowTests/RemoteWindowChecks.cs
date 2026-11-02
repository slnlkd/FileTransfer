extern alias RemoteServer;
extern alias RemoteClient;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Transfer.Core.Remote;
using ClientWindow = RemoteClient::RemoteFiles.Client.MainWindow;
using ServerWindow = RemoteServer::RemoteFiles.Server.MainWindow;
using ClientBrowser = RemoteClient::RemoteFiles.Desktop.RemoteBrowser;
using TextViewer = RemoteClient::RemoteFiles.Desktop.TextViewer;
using ImageViewer = RemoteClient::RemoteFiles.Desktop.ImageViewer;
using ProcessViewer = RemoteClient::RemoteFiles.Desktop.ProcessViewer;

internal static class RemoteWindowChecks
{
    public static async Task RunAsync(string temp)
    {
        string root = Path.Combine(temp, "remote-ui"); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "文本"));
        await File.WriteAllTextAsync(Path.Combine(root, "文本", "编辑.json"), "{\"值\":1}");
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.dat"), "其他类型");
        foreach (string name in new[] { "late.png", "early.png" })
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, name)); encoder.Save(output);
        }
        File.SetCreationTimeUtc(Path.Combine(root, "early.png"), DateTime.UtcNow.AddDays(-1));
        string serverConfig = Path.Combine(temp, "remote-server-ui.json");
        var server = new ServerWindow(serverConfig);
        Prepare(server); server.Show();
        ((TextBox)server.FindName("RootBox")).Text = root;
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        ((TextBox)server.FindName("PortBox")).Text = port.ToString();
        Click(server, "StartButton");
        var serverBrowser = (UserControl)server.FindName("Browser");
        await WaitAsync(() => ((DataGrid)serverBrowser.FindName("FileGrid")).Items.Count == 4);
        Console.WriteLine("通过：新服务端启动并在本地浏览共享根目录");
        string blocked = Path.Combine(temp, "settings-blocked");
        await File.WriteAllTextAsync(blocked, "file, not directory");
        var blockedClient = new ClientWindow(Path.Combine(blocked, "client.json")); Prepare(blockedClient); blockedClient.Show();
        ((TextBox)blockedClient.FindName("PortBox")).Text = port.ToString(); Click(blockedClient, "ConnectButton");
        await WaitAsync(() => ((Button)blockedClient.FindName("ConnectButton")).IsEnabled);
        if (!((TextBlock)blockedClient.FindName("StatusText")).Text.StartsWith("已连接")) throw new Exception("配置无法保存时不应断开成功的连接");
        await CloseAsync(blockedClient);
        var client = new ClientWindow(Path.Combine(temp, "remote-client-ui.json")); Prepare(client); client.Show();
        ((TextBox)client.FindName("PortBox")).Text = port.ToString(); Click(client, "ConnectButton");
        var browser = (ClientBrowser)client.FindName("Browser"); var grid = (DataGrid)browser.FindName("FileGrid");
        var tree = (TreeView)browser.FindName("FolderTree");
        await WaitAsync(() => grid.Items.Count == 4 && ((Button)client.FindName("ConnectButton")).IsEnabled);
        if (grid.Columns[0].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new Exception("初次连接未默认按名称升序排序");
        CheckNamePresentation(grid);
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        var treeRoot = (TreeViewItem)tree.Items[0];
        var textFolder = treeRoot.Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "文本");
        textFolder.IsExpanded = true;
        await WaitAsync(() => textFolder.Items.Count == 1 && textFolder.Items[0] is TreeViewItem);
        textFolder.IsSelected = true;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count == 1);
        Console.WriteLine("通过：客户端连接、左侧树展开与文件夹选择更新右侧列表");
        grid.UpdateLayout(); grid.SelectedIndex = 0;
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
        await WaitAsync(() => client.OwnedWindows.OfType<TextViewer>().Any());
        var textWindow = client.OwnedWindows.OfType<TextViewer>().Single();
        textWindow.Editor.Text = "{\"值\":2}";
        await textWindow.SaveAsync();
        if (await File.ReadAllTextAsync(Path.Combine(root, "文本", "编辑.json")) != "{\"值\":2}") throw new Exception("文本界面保存内容未落盘：" + ((DockPanel)textWindow.Content).Children.OfType<TextBlock>().Single().Text);
        textWindow.Close();
        Console.WriteLine("通过：右侧文件双击打开 JSON 并编辑保存至服务端");
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        var images = (await new RemoteFileClient("127.0.0.1", port).CallAsync(new("list"))).Entries!;
        await browser.OpenAsync(images.Single(e => e.Name == "early.png"));
        var imageWindow = client.OwnedWindows.OfType<ImageViewer>().Single();
        await WaitAsync(() => FindImage(imageWindow)?.Source != null);
        CheckImageName(imageWindow, "early.png");
        var previousArrow = FindButton(imageWindow, "上一张图片") ?? throw new Exception("缺少左侧图片切换箭头");
        var nextArrow = FindButton(imageWindow, "下一张图片") ?? throw new Exception("缺少右侧图片切换箭头");
        if (previousArrow.IsEnabled || !nextArrow.IsEnabled) throw new Exception("首张图片时箭头状态错误");
        nextArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && FindImage(imageWindow)?.Source != null && !nextArrow.IsEnabled);
        if (!previousArrow.IsEnabled) throw new Exception("末张图片时左侧箭头未启用");
        previousArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && FindImage(imageWindow)?.Source != null && !previousArrow.IsEnabled);
        imageWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(imageWindow), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && FindImage(imageWindow)?.Source != null);
        CheckImageName(imageWindow, "late.png");
        imageWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(imageWindow), 0, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && FindImage(imageWindow)?.Source != null);
        imageWindow.Close();
        Console.WriteLine("通过：图片真实解码，Right / Left 按创建时间前后切换");
        int owned = client.OwnedWindows.Count;
        await browser.OpenAsync(images.Single(e => e.Name == "ignored.dat"));
        if (client.OwnedWindows.Count != owned) throw new Exception("其他类型不应创建查看窗口");
        Console.WriteLine("通过：其他类型仅列出，不打开编辑或预览");
        await ArchiveUiAsync(browser, server, root, temp);
        string sortRoot = Path.Combine(root, "排序测试");
        Directory.CreateDirectory(sortRoot);
        foreach (string name in new[] { "10", "2", "1" }) Directory.CreateDirectory(Path.Combine(sortRoot, name));
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        var sortFolder = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "排序测试");
        await browser.OpenAsync(sortFolder);
        if (!grid.Items.Cast<RemoteEntry>().Select(e => e.Name).SequenceEqual(new[] { "1", "2", "10" }))
            throw new Exception("进入文件夹后未按名称自然升序排列：" + string.Join(",", grid.Items.Cast<RemoteEntry>().Select(e => e.Name)) + "；方向：" + grid.Columns[0].SortDirection);
        await browser.OpenAsync(sortFolder);
        if (!grid.Items.Cast<RemoteEntry>().Select(e => e.Name).SequenceEqual(new[] { "1", "2", "10" }))
            throw new Exception("重新加载目录后未保持名称自然升序");
        Console.WriteLine("通过：进入和重新加载文件夹时默认按名称自然升序");
        int rootCount = Directory.GetFileSystemEntries(root).Length;
        await CloseAsync(client);
        Click(server, "StopButton");
        await WaitAsync(() => ((Button)server.FindName("StartButton")).IsEnabled);
        Click(server, "StartButton");
        await WaitAsync(() => ((DataGrid)serverBrowser.FindName("FileGrid")).Items.Count == rootCount);
        await CloseAsync(server);
        Console.WriteLine("通过：新文件管理服务停止、重新启动与窗口关闭");
    }
    private static async Task ArchiveUiAsync(ClientBrowser browser, ServerWindow server, string root, string temp)
    {
        var tree = (TreeView)browser.FindName("FolderTree");
        var grid = (DataGrid)browser.FindName("FileGrid");
        var rootNode = (TreeViewItem)tree.Items[0];
        var folder = rootNode.Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "文本");
        if (folder.ContextMenu?.Items.Count != 2 || ((MenuItem)folder.ContextMenu.Items[0]).Header?.ToString() != "添加压缩包") throw new Exception("目录树缺少添加压缩包菜单");
        ((MenuItem)folder.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitAsync(() => !browser.IsBusy && File.Exists(Path.Combine(root, "文本.zip")) && grid.Items.Cast<RemoteEntry>().Any(e => e.IsZip));
        if (((ProgressBar)browser.FindName("UploadProgress")).Value != 100) throw new Exception("压缩完成后客户端进度不是 100%");
        Console.WriteLine("通过：目录树右键添加压缩包，在服务端生成 ZIP 并刷新列表和进度");
        var zip = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "文本.zip");
        var directory = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "文本");
        grid.SelectedItem = zip; grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(directory);
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
        // 实际打开行菜单，确认菜单项对应被右键的文件夹而非原选中的 ZIP。
        if (!ReferenceEquals(grid.SelectedItem, directory)) throw new Exception("列表右键未选中对应行");
        // 触发框架的 ContextMenuOpening 路由事件，使用用户点击时的处理器。
        OpenContext(row);
        if (row.ContextMenu.Items.Count != 2 || ((MenuItem)row.ContextMenu.Items[0]).Header?.ToString() != "添加压缩包") throw new Exception("文件列表目录菜单错误");
        var zipNode = ((TreeViewItem)tree.Items[0]).Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "文本.zip");
        if (zipNode.ContextMenu.Items.Count != 4 || ((MenuItem)zipNode.ContextMenu.Items[1]).Header?.ToString() != "下载 ZIP…" || ((MenuItem)zipNode.ContextMenu.Items[3]).Header?.ToString() != "删除服务端文件…") throw new Exception("ZIP 下载或删除菜单错误");
        string output = Path.Combine(temp, "ui-zip-download.zip");
        await browser.DownloadEntryAsync(zip, output);
        if (!File.Exists(output) || ((ProgressBar)browser.FindName("UploadProgress")).Value != 100) throw new Exception("界面 ZIP 下载未完成");
        await WaitAsync(() => ((DataGrid)server.FindName("TransferGrid")).Items.Cast<RemoteTransferProgress>().Any(p => p.Stage == "下载完成"));
        Console.WriteLine("通过：列表右键选中正确文件，ZIP 下载校验成功，服务端进度列表收到完成状态");
        string folderOutput = Path.Combine(temp, "ui-folder-download.zip");
        await browser.DownloadEntryAsync(directory, folderOutput);
        if (!File.Exists(folderOutput) || !File.Exists(Path.Combine(root, "文本 (1).zip"))) throw new Exception("文件夹打包下载失败");
        var localBrowser = (RemoteServer::RemoteFiles.Desktop.RemoteBrowser)server.FindName("Browser");
        string localOutput = Path.Combine(temp, "server-folder-download.zip");
        await localBrowser.DownloadEntryAsync(directory, localOutput);
        if (!File.Exists(localOutput)) throw new Exception("服务端本地浏览器文件夹打包下载失败");
        Console.WriteLine("通过：客户端和服务端浏览器均可指定文件夹打包下载");
        var ignored = ((TreeViewItem)tree.Items[0]).Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "ignored.dat");
        ((MenuItem)ignored.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitAsync(() => !browser.IsBusy && grid.Items.Cast<RemoteEntry>().Any(e => e.Name == "ignored.dat.zip"));
        var extra = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "ignored.dat.zip");
        await browser.DeleteEntryAsync(extra);
        if (File.Exists(Path.Combine(root, extra.Path)) || grid.Items.Cast<RemoteEntry>().Any(e => e.Path == extra.Path) || !File.Exists(Path.Combine(root, "ignored.dat"))) throw new Exception("界面删除 ZIP 或刷新失败");
        Console.WriteLine("通过：普通文件右键生成 ZIP，删除多余 ZIP 后刷新列表且保留源文件");
    }
    private static void OpenContext(DataGridRow row)
    {
        // ContextMenuEventArgs 构造器非公开，使用框架事件参数触发相同的路由处理。
        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { row, true, 0d, 0d }, null)!;
        row.RaiseEvent(args);
    }
    private static void CheckImageName(ImageViewer window, string name)
    {
        var blocks = FindTextBlocks(window).ToArray();
        if (!blocks.Any(b => b.FontWeight == FontWeights.Bold && b.Text == name))
            throw new Exception("顶部未显示加粗的图片名称：" + name);
        if (!blocks.Any(b => b.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.FontWeight == FontWeights.Bold && r.Text == name)))
            throw new Exception("底部未显示加粗的图片名称：" + name);
        if (!blocks.Any(b => b.FontWeight == FontWeights.Bold && b.Text == window.Title))
            throw new Exception("标题栏未加粗显示图片路径");
        if (!blocks.Any(b => b.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.FontWeight == FontWeights.Bold && r.Text.Contains("/"))))
            throw new Exception("底部序号和路径未加粗");
    }
    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject parent)
    {
        if (parent is TextBlock block) yield return block;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in FindTextBlocks(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void CheckNamePresentation(DataGrid grid)
    {
        string[] names = ["10", "2", "1", "A6-DPQG-020", "A6-DPQG-009"];
        grid.Columns[0].SortDirection = null;
        grid.ItemsSource = names.Select(n => new RemoteEntry(n, n, true, 0, default, default)).ToArray();
        grid.UpdateLayout();
        var header = FindHeader(grid, "名称") ?? throw new Exception("找不到名称列标题");
        var click = typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        click.Invoke(header, null);
        if (!grid.Items.Cast<RemoteEntry>().Select(e => e.Name).SequenceEqual(new[] { "1", "2", "10", "A6-DPQG-009", "A6-DPQG-020" }))
            throw new Exception("名称升序未按数字大小排序");
        click.Invoke(header, null);
        if (!grid.Items.Cast<RemoteEntry>().Select(e => e.Name).SequenceEqual(new[] { "A6-DPQG-020", "A6-DPQG-009", "10", "2", "1" }))
            throw new Exception("名称降序未按数字大小排序");
        grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        if (FindImage(row)?.Source == null) throw new Exception("名称列缺少文件夹图标");
        var icons = new RemoteClient::RemoteFiles.Desktop.FileIconConverter();
        foreach (string name in new[] { "图片.jpg", "图片.png", "文件.txt", "应用.exe", "压缩.zip", "未知.unknown123", "无扩展名" })
            if (icons.Convert(new RemoteEntry(name, name, false, 0, default, default), typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture) is not ImageSource)
                throw new Exception("缺少文件类型图标：" + name);
        var typeHeader = FindHeader(grid, "类型") ?? throw new Exception("找不到类型列标题");
        click.Invoke(typeHeader, null);
        if (grid.Columns[1].SortDirection == null) throw new Exception("切换其他列后无法排序");
        Console.WriteLine("通过：名称自然排序、其他列排序及常见文件类型图标");
    }
    private static System.Windows.Controls.Primitives.DataGridColumnHeader? FindHeader(DependencyObject parent, string label)
    {
        if (parent is System.Windows.Controls.Primitives.DataGridColumnHeader header && Equals(header.Content, label)) return header;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindHeader(VisualTreeHelper.GetChild(parent, i), label) is { } child) return child;
        return null;
    }
    private static Image? FindImage(DependencyObject parent)
    {
        if (parent is Image image) return image;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindImage(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private static Button? FindButton(DependencyObject parent, string tooltip)
    {
        if (parent is Button button && Equals(button.ToolTip, tooltip)) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindButton(VisualTreeHelper.GetChild(parent, i), tooltip) is { } child) return child;
        return null;
    }
    private static void Prepare(Window window) { window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -32000; window.Top = -32000; }
    private static void Click(FrameworkElement window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private static async Task CloseAsync(Window window)
    {
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

