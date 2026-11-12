extern alias RemoteServer;
extern alias RemoteClient;
using System.IO;
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
        var rootOrder = grid.Items.Cast<RemoteEntry>().ToArray();
        if (rootOrder.SkipWhile(e => e.IsDirectory).Any(e => e.IsDirectory))
            throw new Exception("按名称排序未把文件夹排在文件前");
        if (grid.Columns[0].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new Exception("初次连接未默认按名称升序排序");
        CheckNamePresentation(grid);
        var selectionText = (TextBlock)client.FindName("SelectionText");
        grid.SelectedItems.Add(grid.Items[0]);
        grid.SelectedItems.Add(grid.Items[1]);
        await WaitAsync(() => selectionText.Text.Contains("已选择 2 项"));
        grid.SelectedItems.Clear();
        await WaitAsync(() => selectionText.Text == "");
        Console.WriteLine("通过：状态栏显示已选择数量");
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        var treeRoot = (TreeViewItem)tree.Items[0];
        var textFolder = treeRoot.Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "文本");
        textFolder.IsExpanded = true;
        await WaitAsync(() => textFolder.Items.Count == 1 && textFolder.Items[0] is TreeViewItem);
        textFolder.IsSelected = true;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count == 1);
        Console.WriteLine("通过：客户端连接、左侧树展开与文件夹选择更新右侧列表");
        var dragData = new RemoteClient::RemoteFiles.Desktop.RemoteFileDataObject(
            new RemoteFileClient("127.0.0.1", port),
            new[] { new RemoteEntry("文本", "文本", true, 0, DateTime.UtcNow, DateTime.UtcNow) });
        if (!dragData.DescriptorNames().Contains("文本\\编辑.json")) throw new Exception("拖出数据未包含子文件描述");
        var contents = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = (short)RemoteClient::RemoteFiles.Desktop.RemoteFileDataObject.ContentsFormatId,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = dragData.DescriptorNames().ToList().IndexOf("文本\\编辑.json"),
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM
        };
        dragData.GetData(ref contents, out var medium);
        if (medium.tymed != System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM) throw new Exception("拖出内容不是流格式");
        var dragStream = (System.Runtime.InteropServices.ComTypes.IStream)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(medium.unionmember);
        var dragBytes = new byte[256];
        IntPtr dragRead = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            dragStream.Read(dragBytes, dragBytes.Length, dragRead);
            int length = System.Runtime.InteropServices.Marshal.ReadInt32(dragRead);
            if (!System.Text.Encoding.UTF8.GetString(dragBytes, 0, length).Contains("值")) throw new Exception("拖出流内容不正确");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(dragRead);
            System.Runtime.InteropServices.Marshal.Release(medium.unionmember);
        }
        Console.WriteLine("通过：拖出时按需读取远程文件内容");
        IntPtr dragComPtr = System.Runtime.InteropServices.Marshal.GetComInterfaceForObject(
            dragData, typeof(System.Runtime.InteropServices.ComTypes.IDataObject));
        if (dragComPtr == IntPtr.Zero) throw new Exception("拖出数据对象无法提供 COM 接口");
        var dragCom = (System.Runtime.InteropServices.ComTypes.IDataObject)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(dragComPtr);
        var descriptorQuery = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = (short)RemoteClient::RemoteFiles.Desktop.RemoteFileDataObject.DescriptorFormatId,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
        };
        if (dragCom.QueryGetData(ref descriptorQuery) != 0) throw new Exception("COM 数据对象不支持文件描述格式");
        System.Runtime.InteropServices.Marshal.Release(dragComPtr);
        Console.WriteLine("通过：拖出数据对象可跨 COM 边界访问");
        Console.WriteLine("通过：拖出时生成远程文件描述");
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
        grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(client), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => grid.Items.Count == 4 && ((Button)client.FindName("ConnectButton")).IsEnabled);
        Console.WriteLine("通过：文件列表退格键返回上一级");
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        var images = (await new RemoteFileClient("127.0.0.1", port).CallAsync(new("list"))).Entries!;
        await browser.OpenAsync(images.Single(e => e.Name == "early.png"));
        var imageWindow = client.OwnedWindows.OfType<ImageViewer>().Single();
        await WaitAsync(() => FindImage(imageWindow)?.Source != null);
        CheckImageName(imageWindow, "early.png");
        var previousArrow = FindButton(imageWindow, "上一张图片") ?? throw new Exception("缺少左侧图片切换箭头");
        var nextArrow = FindButton(imageWindow, "下一张图片") ?? throw new Exception("缺少右侧图片切换箭头");
        if (FindButtonByContent(imageWindow, "← 上一张") is not null || FindButtonByContent(imageWindow, "下一张 →") is not null)
            throw new Exception("不应保留上一张/下一张文本按钮");
        if (previousArrow.IsEnabled || !nextArrow.IsEnabled) throw new Exception("首张图片时箭头状态错误");
        var thumbList = FindDescendant<ListBox>(imageWindow) ?? throw new Exception("缺少底部缩略图条");
        if (thumbList.Items.Count != 2) throw new Exception("缩略图数量错误：" + thumbList.Items.Count);
        var pageBox = FindDescendant<TextBox>(imageWindow) ?? throw new Exception("缺少页码输入框");
        if (pageBox.Text != "1") throw new Exception("初始页码错误：" + pageBox.Text);
        // 页码跳转
        pageBox.Text = "2";
        pageBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(imageWindow), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && !nextArrow.IsEnabled && previousArrow.IsEnabled);
        pageBox.Text = "1";
        pageBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(imageWindow), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && !previousArrow.IsEnabled && nextArrow.IsEnabled);
        // 缩略图点击切换
        thumbList.SelectedIndex = 1;
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && !nextArrow.IsEnabled);
        thumbList.SelectedIndex = 0;
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && !previousArrow.IsEnabled);
        Console.WriteLine("通过：底部缩略图、页码跳转与点击切换");
        nextArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && FindImage(imageWindow)?.Source != null && !nextArrow.IsEnabled && previousArrow.IsEnabled);
        if (!previousArrow.IsEnabled) throw new Exception("末张图片时左侧箭头未启用");
        previousArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && FindImage(imageWindow)?.Source != null && !previousArrow.IsEnabled && nextArrow.IsEnabled);
        imageWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(imageWindow), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && FindImage(imageWindow)?.Source != null && !nextArrow.IsEnabled && previousArrow.IsEnabled);
        CheckImageName(imageWindow, "late.png");
        imageWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(imageWindow), 0, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && FindImage(imageWindow)?.Source != null && !previousArrow.IsEnabled && nextArrow.IsEnabled);
        imageWindow.Close();
        Console.WriteLine("通过：图片真实解码，Right / Left 按创建时间前后切换");
        // 缩略图滚动到末尾仍应按需加载，不能出现空白。
        string thumbFolder = Path.Combine(root, "缩略图测试");
        Directory.CreateDirectory(thumbFolder);
        byte[] sampleImage = await File.ReadAllBytesAsync(Path.Combine(root, "early.png"));
        for (int i = 1; i <= 25; i++) await File.WriteAllBytesAsync(Path.Combine(thumbFolder, $"{i:00}.png"), sampleImage);
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "缩略图测试"));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().First());
        var thumbWindow = client.OwnedWindows.OfType<ImageViewer>().Last();
        await WaitAsync(() => FindImage(thumbWindow)?.Source != null);
        var thumbStrip = FindDescendant<ListBox>(thumbWindow) ?? throw new Exception("缺少底部缩略图条");
        if (thumbStrip.Items.Count != 25) throw new Exception("缩略图数量错误：" + thumbStrip.Items.Count);
        if (FindButtonByContent(thumbWindow, "定位") is null) throw new Exception("缺少定位当前图片按钮");
        var thumbScroll = FindDescendant<ScrollViewer>(thumbStrip) ?? throw new Exception("缩略图条缺少滚动视图");
        thumbStrip.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        await WaitAsync(() => thumbScroll.HorizontalOffset > 0);
        if (thumbScroll.ScrollableWidth > 0 && thumbScroll.HorizontalOffset >= thumbScroll.ScrollableWidth - 1)
            throw new Exception("缩略图滚轮一次就跳到了末尾");
        var lastThumb = (RemoteClient::RemoteFiles.Desktop.ImageThumbItem)thumbStrip.Items[24];
        thumbStrip.ScrollIntoView(lastThumb);
        await WaitAsync(() => lastThumb.Thumbnail != null);
        thumbWindow.Close();
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        Console.WriteLine("通过：缩略图滚动到末尾仍按需加载");
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
        string scrollRoot = Path.Combine(root, "滚动测试");
        Directory.CreateDirectory(scrollRoot);
        foreach (string name in Enumerable.Range(1, 60).Select(i => i.ToString("D2"))) Directory.CreateDirectory(Path.Combine(scrollRoot, name));
        Directory.CreateDirectory(Path.Combine(scrollRoot, "大目录"));
        foreach (string name in Enumerable.Range(1, 60).Select(i => i.ToString("D2"))) Directory.CreateDirectory(Path.Combine(scrollRoot, "大目录", name));
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "滚动测试"));
        await FlushAsync(browser);
        grid.UpdateLayout();
        var scroll = FindScrollViewer(grid) ?? throw new Exception("未找到文件列表滚动条");
        if (scroll.ScrollableHeight <= 0) throw new Exception("测试目录不足以产生滚动条");
        scroll.ScrollToBottom();
        await WaitAsync(() => scroll.VerticalOffset > 0);
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "大目录"));
        await FlushAsync(browser);
        await WaitAsync(() => scroll.VerticalOffset == 0);
        grid.UpdateLayout();
        if (scroll.ScrollableHeight <= 0) throw new Exception("切换后的目录不足以产生滚动条");
        scroll.ScrollToBottom();
        await WaitAsync(() => scroll.VerticalOffset > 0);
        var scrollNode = ((TreeViewItem)tree.Items[0]).Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "滚动测试");
        scrollNode.IsSelected = true;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count > 1);
        await FlushAsync(browser);
        await WaitAsync(() => scroll.VerticalOffset == 0);
        Console.WriteLine("通过：切换目录和返回上一级后文件列表回到顶部");
        var bigFolder = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "大目录");
        await browser.OpenAsync(bigFolder);
        await FlushAsync(browser);
        grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(client), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => !browser.IsBusy && grid.SelectedItem is RemoteEntry { Name: "大目录" });
        await FlushAsync(browser);
        grid.UpdateLayout();
        var backScroll = FindScrollViewer(grid) ?? throw new Exception("未找到文件列表滚动条");
        if (backScroll.VerticalOffset <= 0) throw new Exception("返回上一级后未定位到刚才的文件夹");
        Console.WriteLine("通过：返回上一级定位到刚才所在文件夹");
        string pageRoot = Path.Combine(root, "分页测试");
        Directory.CreateDirectory(pageRoot);
        for (int i = 1; i <= 620; i++) await File.WriteAllTextAsync(Path.Combine(pageRoot, i + ".txt"), "");
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "分页测试"));
        await FlushAsync(browser);
        if (grid.Items.Count != 500) throw new Exception("首屏未按 500 条分页加载：" + grid.Items.Count);
        var countText = (TextBlock)browser.FindName("ItemCountText");
        if (countText.Text != "500 / 620 项") throw new Exception("分页计数显示错误：" + countText.Text);
        var selectAll = typeof(ClientBrowser).GetMethod("SelectAllAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new Exception("找不到全选方法");
        await (Task)selectAll.Invoke(browser, null)!;
        await WaitAsync(() => grid.SelectedItems.Count == 620 && grid.Items.Count == 620);
        grid.SelectedItems.Clear();
        Console.WriteLine("通过：Ctrl+A 会先加载全部条目再全选");
        var pageScroll = FindScrollViewer(grid) ?? throw new Exception("未找到文件列表滚动条");
        pageScroll.ScrollToBottom();
        await WaitAsync(() => grid.Items.Count == 620);
        if (countText.Text != "620 项") throw new Exception("滚动加载完成后计数错误：" + countText.Text);
        Console.WriteLine("通过：大目录按 500 条分页并在滚动到底部时加载更多");
        ((TreeViewItem)tree.Items[0]).IsSelected = true;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count > 1);
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "分页测试"));
        await FlushAsync(browser);
        if (grid.Items.Count != 500) throw new Exception("重新进入目录后未回到分页首屏");
        var sizeHeader = FindHeader(grid, "大小") ?? throw new Exception("找不到大小列标题");
        var sortClick = typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        sortClick.Invoke(sizeHeader, null);
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count == 620);
        Console.WriteLine("通过：分页状态下切换排序会先加载全部条目");
        var execute = typeof(ClientBrowser).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(Func<CancellationToken, Task>), typeof(bool) }, null) ?? throw new Exception("找不到可取消操作入口");
        var slow = (Task)execute.Invoke(browser, new object[]
        {
            new Func<CancellationToken, Task>(async token => await Task.Delay(5000, token)), true
        })!;
        var cancelButton = (Button)browser.FindName("CancelButton");
        await WaitAsync(() => cancelButton.Visibility == Visibility.Visible);
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await slow;
        if (((TextBlock)browser.FindName("StatusText")).Text != "已取消" || browser.IsBusy || cancelButton.Visibility != Visibility.Collapsed)
            throw new Exception("取消后状态或界面未恢复");
        Console.WriteLine("通过：长操作可取消并恢复界面");
        // 过滤只作用于右侧列表，目录树必须保持完整；重新加载根目录时同样如此。
        var service = (RemoteFileService)typeof(ServerWindow).GetField("service", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        service.ListFilter = (_, _, items) => Task.FromResult(new RemoteFilterResult(items.Where(e => e.Name != "文本").ToArray()));
        var filterField = typeof(ClientBrowser).GetField("listFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        filterField.SetValue(browser, "测试");
        var navigate = typeof(ClientBrowser).GetMethod("NavigateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(bool), typeof(CancellationToken), typeof(bool) }, null)!;
        await (Task)navigate.Invoke(browser, ["", true, CancellationToken.None, false])!;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count > 0);
        if (grid.Items.Cast<RemoteEntry>().Any(e => e.Name == "文本")) throw new Exception("过滤未作用于右侧列表");
        var treeRootNode = (TreeViewItem)tree.Items[0];
        if (!treeRootNode.Items.Cast<TreeViewItem>().Any(n => ((RemoteEntry)n.Tag).Name == "文本"))
            throw new Exception("过滤时目录树丢失了未过滤的文件夹");
        Console.WriteLine("通过：过滤只影响右侧列表，目录树保持完整");
        service.ListFilter = null; filterField.SetValue(browser, null);
        await (Task)navigate.Invoke(browser, ["", true, CancellationToken.None, false])!;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Cast<RemoteEntry>().Any(e => e.Name == "文本"));
        string draggedLocal = Path.Combine(temp, "拖入测试.txt");
        await File.WriteAllTextAsync(draggedLocal, "拖入内容");
        var dropData = new DataObject(DataFormats.FileDrop, new[] { draggedLocal });
        var dropArgs = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object[] { dropData, DragDropKeyStates.None, DragDropEffects.Copy, browser, new Point(10, 10) }, null)!;
        dropArgs.RoutedEvent = DragDrop.DropEvent;
        ((FrameworkElement)browser.FindName("FileArea")).RaiseEvent(dropArgs);
        await WaitAsync(() => !browser.IsBusy && grid.Items.Cast<RemoteEntry>().Any(e => e.Name == "拖入测试.txt"));
        if (await File.ReadAllTextAsync(Path.Combine(root, "拖入测试.txt")) != "拖入内容") throw new Exception("拖入文件未上传到当前目录");
        Console.WriteLine("通过：从本地拖入文件上传到当前目录");
        string dragFolder = Path.Combine(temp, "拖入文件夹");
        Directory.CreateDirectory(dragFolder);
        await File.WriteAllTextAsync(Path.Combine(dragFolder, "子文件.txt"), "子内容");
        var folderDropData = new DataObject(DataFormats.FileDrop, new[] { dragFolder });
        var folderDropArgs = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object[] { folderDropData, DragDropKeyStates.None, DragDropEffects.Copy, browser, new Point(10, 10) }, null)!;
        folderDropArgs.RoutedEvent = DragDrop.DropEvent;
        ((FrameworkElement)browser.FindName("FileArea")).RaiseEvent(folderDropArgs);
        await WaitAsync(() => !browser.IsBusy && grid.Items.Cast<RemoteEntry>().Any(e => e.Name == "拖入文件夹"));
        if (await File.ReadAllTextAsync(Path.Combine(root, "拖入文件夹", "子文件.txt")) != "子内容") throw new Exception("拖入文件夹未保留子文件");
        Console.WriteLine("通过：从本地拖入文件夹并保留子文件");
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
        if (folder.ContextMenu?.Items.Count != 1 || ((MenuItem)folder.ContextMenu.Items[0]).Header?.ToString() != "复制") throw new Exception("目录树右键菜单应为复制");
        await browser.CopyToClipboardAsync((RemoteEntry)folder.Tag);
        var firstDrop = Clipboard.GetDataObject()?.GetData(DataFormats.FileDrop) as string[];
        if (firstDrop is null || firstDrop.Length != 1) throw new Exception("剪贴板未包含复制内容");
        string firstSession = Path.GetDirectoryName(firstDrop[0])!;
        // 多选复制：两个条目应在剪贴板中各自成为独立项。
        if (grid.SelectionMode != DataGridSelectionMode.Extended) throw new Exception("文件列表未启用多选");
        var ignoredEntry = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "ignored.dat");
        await browser.CopyToClipboardAsync(new[] { (RemoteEntry)folder.Tag, ignoredEntry });
        if (Directory.Exists(firstSession)) throw new Exception("再次复制时未清理上一次的剪贴板临时目录");
        if (!((TextBlock)browser.FindName("StatusText")).Text.Contains("已复制")) throw new Exception("复制后状态提示错误");
        var dropped = Clipboard.GetDataObject()?.GetData(DataFormats.FileDrop) as string[];
        if (dropped is null || dropped.Length != 2 || !dropped.Any(p => File.Exists(Path.Combine(p, "编辑.json"))) || !dropped.Any(p => Path.GetFileName(p) == "ignored.dat"))
            throw new Exception("剪贴板未包含多选复制的内容");
        Clipboard.Clear();
        // 测试结束后清理本次进程的剪贴板目录，避免留在本机 LocalAppData。
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dropped[0])!)!, true); } catch (Exception ex) { Console.WriteLine("清理测试剪贴板目录失败：" + ex.Message); }
        if (File.Exists(Path.Combine(root, "文本.zip"))) throw new Exception("复制后未清理服务端临时 ZIP");
        Console.WriteLine("通过：右键复制文件夹，剪贴板可在资源管理器粘贴且清理服务端临时 ZIP");
        await browser.AddArchiveAsync((RemoteEntry)folder.Tag);
        await WaitAsync(() => !browser.IsBusy && File.Exists(Path.Combine(root, "文本.zip")) && grid.Items.Cast<RemoteEntry>().Any(e => e.IsZip));
        if (((ProgressBar)browser.FindName("UploadProgress")).Value != 100) throw new Exception("压缩完成后客户端进度不是 100%");
        Console.WriteLine("通过：服务端压缩 ZIP 并刷新列表和进度");
        var zip = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "文本.zip");
        var directory = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "文本");
        grid.SelectedItem = zip; grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(directory);
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
        // 实际打开行菜单，确认菜单项对应被右键的文件夹而非原选中的 ZIP。
        if (!ReferenceEquals(grid.SelectedItem, directory)) throw new Exception("列表右键未选中对应行");
        // 触发框架的 ContextMenuOpening 路由事件，使用用户点击时的处理器。
        OpenContext(row);
        if (row.ContextMenu.Items.Count != 1 || ((MenuItem)row.ContextMenu.Items[0]).Header?.ToString() != "复制") throw new Exception("文件列表目录菜单错误");
        var zipNode = ((TreeViewItem)tree.Items[0]).Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "文本.zip");
        if (zipNode.ContextMenu.Items.Count != 3 || ((MenuItem)zipNode.ContextMenu.Items[0]).Header?.ToString() != "复制" || ((MenuItem)zipNode.ContextMenu.Items[2]).Header?.ToString() != "删除服务端文件…") throw new Exception("ZIP 复制或删除菜单错误");
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
        await browser.AddArchiveAsync((RemoteEntry)ignored.Tag);
        await WaitAsync(() => !browser.IsBusy && grid.Items.Cast<RemoteEntry>().Any(e => e.Name == "ignored.dat.zip"));
        var extra = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "ignored.dat.zip");
        await browser.DeleteEntryAsync(extra);
        if (File.Exists(Path.Combine(root, extra.Path)) || grid.Items.Cast<RemoteEntry>().Any(e => e.Path == extra.Path) || !File.Exists(Path.Combine(root, "ignored.dat"))) throw new Exception("界面删除 ZIP 或刷新失败");
        Console.WriteLine("通过：普通文件生成 ZIP，删除多余 ZIP 后刷新列表且保留源文件");
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
        // 名称和路径只应出现在顶部工具栏一处，底部只显示创建时间、尺寸和大小。
        int occurrences = blocks.Count(b => b.Text.Contains(name));
        if (occurrences != 1) throw new Exception($"图片名称应只出现一次，实际 {occurrences} 次：{name}");
        if (!blocks.Any(b => b.Inlines.OfType<Run>().Any(r => r.FontWeight == FontWeights.Bold && r.Text == name)))
            throw new Exception("顶部未加粗显示图片名称：" + name);
        if (!blocks.Any(b => b.Inlines.OfType<Run>().Any(r => r.Text.StartsWith("创建："))))
            throw new Exception("底部未显示创建时间");
        if (!blocks.Any(b => b.Inlines.OfType<Run>().Any(r => r.Text.Contains("×"))))
            throw new Exception("底部未显示图片尺寸");
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
        header.UpdateLayout();
        var nameArrow = header.Template.FindName("SortArrow", header) as System.Windows.Shapes.Path;
        if (nameArrow?.Visibility != Visibility.Visible) throw new Exception("名称升序未显示排序箭头");
        if (!grid.Items.Cast<RemoteEntry>().Select(e => e.Name).SequenceEqual(new[] { "1", "2", "10", "A6-DPQG-009", "A6-DPQG-020" }))
            throw new Exception("名称升序未按数字大小排序");
        click.Invoke(header, null);
        header.UpdateLayout();
        if (nameArrow?.Visibility != Visibility.Visible) throw new Exception("名称降序未显示排序箭头");
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
        typeHeader.UpdateLayout();
        header.UpdateLayout();
        var typeArrow = typeHeader.Template.FindName("SortArrow", typeHeader) as System.Windows.Shapes.Path;
        if (typeArrow?.Visibility != Visibility.Visible || nameArrow?.Visibility != Visibility.Collapsed)
            throw new Exception("切换排序列后箭头状态错误");
        Console.WriteLine("通过：名称自然排序、其他列排序及常见文件类型图标");
    }
    private static System.Windows.Controls.Primitives.DataGridColumnHeader? FindHeader(DependencyObject parent, string label)
    {
        if (parent is System.Windows.Controls.Primitives.DataGridColumnHeader header && Equals(header.Content, label)) return header;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindHeader(VisualTreeHelper.GetChild(parent, i), label) is { } child) return child;
        return null;
    }
    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private static Image? FindImage(DependencyObject parent)    {
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
    private static Button? FindButtonByContent(DependencyObject parent, string content)
    {
        if (parent is Button button && Equals(button.Content, content)) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindButtonByContent(VisualTreeHelper.GetChild(parent, i), content) is { } child) return child;
        return null;
    }
    private static void Prepare(Window window) { window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -32000; window.Top = -32000; }
    private static void Click(FrameworkElement window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private static async Task FlushAsync(System.Windows.Threading.DispatcherObject target)
    {
        await target.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
    private static async Task CloseAsync(Window window)
    {
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

