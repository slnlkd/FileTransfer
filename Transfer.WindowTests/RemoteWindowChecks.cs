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

internal static class RemoteWindowChecks
{
    public static async Task RunAsync(string temp)
    {
        CheckLegacyClientSettingsMigration(temp);
        await CheckAnnotationReopenAsync(temp);
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
        ((ComboBox)blockedClient.FindName("EndpointBox")).Text = $"127.0.0.1:{port}"; Click(blockedClient, "ConnectButton");
        await WaitAsync(() => ((Button)blockedClient.FindName("ConnectButton")).IsEnabled);
        if (!((TextBlock)blockedClient.FindName("StatusText")).Text.StartsWith("已连接")) throw new Exception("配置无法保存时不应断开成功的连接");
        await CloseAsync(blockedClient);
        string connectionSettings = Path.Combine(temp, "remote-client-ui.json");
        await File.WriteAllTextAsync(connectionSettings, JsonSerializer.Serialize(new { Host = "localhost", Port = port }));
        var client = new ClientWindow(connectionSettings); Prepare(client); client.Show();
        var endpointBox = (ComboBox)client.FindName("EndpointBox");
        if (endpointBox.Text != $"localhost:{port}" || !endpointBox.IsEditable)
            throw new Exception("可编辑地址下拉框应读取旧版连接设置");
        foreach (int suffix in new[] { 181, 182, 183 })
            if (!endpointBox.Items.Contains($"10.100.40.{suffix}:9501") || endpointBox.Items.Contains($"10.100.40.{suffix}:9502"))
                throw new Exception("三台服务器只能预置 9501 端口");
        endpointBox.SelectedItem = "10.100.40.183:9501";
        if (endpointBox.Text != "10.100.40.183:9501") throw new Exception("选择预设应填入完整地址");
        endpointBox.ApplyTemplate();
        var endpointInput = (TextBox)endpointBox.Template.FindName("PART_EditableTextBox", endpointBox);
        endpointInput.Text = $"127.0.0.1:{port}";
        if (endpointBox.Text != endpointInput.Text) throw new Exception("手动输入应更新待连接的地址和端口");
        Click(client, "ConnectButton");
        var browser = (ClientBrowser)client.FindName("Browser"); var grid = (DataGrid)browser.FindName("FileGrid");
        var tree = (TreeView)browser.FindName("FolderTree");
        await WaitAsync(() => grid.Items.Count == 4 && ((Button)client.FindName("ConnectButton")).IsEnabled);
        var reopenedSettings = new ClientWindow(connectionSettings); Prepare(reopenedSettings); reopenedSettings.Show();
        var reopenedEndpoints = (ComboBox)reopenedSettings.FindName("EndpointBox");
        if (reopenedEndpoints.Text != $"127.0.0.1:{port}" || !reopenedEndpoints.Items.Contains($"localhost:{port}"))
            throw new Exception("应保存并恢复最近连接的自定义地址和端口");
        await CloseAsync(reopenedSettings);
        endpointBox.Text = "127.0.0.1:65536";
        Click(client, "ConnectButton");
        if (!((TextBlock)client.FindName("StatusText")).Text.Contains("65535") || grid.Items.Count != 4)
            throw new Exception("无效端口应提示范围并保留当前目录");
        endpointBox.Text = $"127.0.0.1:{port}";
        Console.WriteLine("通过：可编辑服务端下拉、地址和端口预设、旧配置与最近连接");
        var rootOrder = grid.Items.Cast<RemoteEntry>().ToArray();
        if (rootOrder.SkipWhile(e => e.IsDirectory).Any(e => e.IsDirectory))
            throw new Exception("按名称排序未把文件夹排在文件前");
        if (grid.Columns[0].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new Exception("初次连接未默认按名称升序排序");
        var copyPath = (Button?)browser.FindName("CopyPathButton") ?? throw new Exception("缺少复制完整路径入口");
        copyPath.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string expectedPath = $"127.0.0.1:{port}/{Path.GetFileName(root)}";
        if (Clipboard.GetText() != expectedPath) throw new Exception("复制的完整路径不正确");
        Clipboard.Clear();
        grid.UpdateLayout();
        var nameHeader = FindHeader(grid, "名称") ?? throw new Exception("没有名称表头");
        if (browser.FindName("ColumnSettingsButton") is not null)
            throw new Exception("显示列入口不应占用工具栏");
        var columnMenu = nameHeader.ContextMenu ?? throw new Exception("列设置应由表头右键打开");
        columnMenu.PlacementTarget = nameHeader;
        columnMenu.IsOpen = true;
        await FlushAsync(grid);
        var createdOption = columnMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "创建时间"));
        var createdColumn = grid.Columns.Single(column => column.SortMemberPath == "CreatedLocal");
        var modifiedColumn = grid.Columns.Single(column => column.SortMemberPath == "ModifiedLocal");
        createdOption.IsChecked = false;
        createdOption.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (createdColumn.Visibility != Visibility.Collapsed || modifiedColumn.Visibility != Visibility.Visible)
            throw new Exception("两个时间列应能分别隐藏");
        createdOption.IsChecked = true;
        createdOption.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        columnMenu.IsOpen = false;
        if (createdColumn.Visibility != Visibility.Visible) throw new Exception("隐藏后的时间列应能恢复显示");
        Console.WriteLine("通过：复制当前完整路径与单独控制时间列");
        var selectionText = (TextBlock)client.FindName("SelectionText");
        string rootCountText = $"{grid.Items.Count} 项";
        if (selectionText.Text != rootCountText || browser.FindName("ItemCountText") is not null)
            throw new Exception("文件总数应只显示在状态栏");
        grid.SelectedItems.Add(grid.Items[0]);
        grid.SelectedItems.Add(grid.Items[1]);
        await WaitAsync(() => selectionText.Text.Contains("已选择 2 项"));
        grid.SelectedItems.Clear();
        await WaitAsync(() => selectionText.Text == rootCountText);
        Console.WriteLine("通过：状态栏显示已选择数量");
        CheckNamePresentation(grid);
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
        string updatedText = "{\r\n  \"值\": 2,\r\n  \"备注\": \"值\"\r\n}";
        textWindow.Editor.Text = updatedText;
        textWindow.Editor.CaretIndex = updatedText.IndexOf("备注", StringComparison.Ordinal);
        await FlushAsync(textWindow);
        if (FindTextBlock(textWindow, "第 3 行，第 4 列") is null
            || FindTextBlock(textWindow, "有未保存修改") is null
            || !FindTextBlocks(textWindow).Any(text => text.Text.Contains("UTF", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("文本状态栏应同时显示行列、编码及未保存状态：" +
                string.Join(" | ", FindTextBlocks(textWindow).Where(text => text.IsVisible).Select(text => text.Text)));
        var wordWrap = FindDescendant<CheckBox>(textWindow) ?? throw new Exception("缺少自动换行");
        wordWrap.IsChecked = true;
        await FlushAsync(textWindow);
        if (textWindow.Editor.TextWrapping != TextWrapping.Wrap || FindTextBlock(textWindow, "第 3 行，第 4 列") is null)
            throw new Exception("自动换行应保留逻辑行列");
        wordWrap.IsChecked = false;

        FindButtonByContent(textWindow, "查找")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(textWindow);
        if (FindDescendant<TextBox>(textWindow) is not { IsVisible: true } findInput
            || System.Windows.Automation.AutomationProperties.GetName(findInput) != "查找文本")
            throw new Exception("查找按钮应显示查找输入框");
        findInput.Text = "值";
        if (FindTextBlock(textWindow, "1 / 2") is null) throw new Exception("查找应显示匹配序号与总数");
        FindButton(textWindow, "下一处 (Enter)")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (FindTextBlock(textWindow, "2 / 2") is null) throw new Exception("下一处应更新匹配序号");
        FindButton(textWindow, "关闭查找 (Esc)")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        async Task RespondToTextClose(TextViewer viewer, string choice)
        {
            var reply = viewer.Dispatcher.InvokeAsync(() =>
            {
                var confirmation = viewer.OwnedWindows.Cast<Window>().Single(window => window.IsVisible);
                try
                {
                    foreach (string action in new[] { "保存", "放弃", "取消" })
                        if (FindButtonByContent(confirmation, action) is null) throw new Exception("关闭提示缺少操作：" + action);
                    if (choice == "取消") confirmation.DialogResult = false;
                    else FindButtonByContent(confirmation, choice)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { if (confirmation.IsVisible) confirmation.DialogResult = false; }
            });
            viewer.Close();
            await reply;
        }

        await RespondToTextClose(textWindow, "取消");
        if (!textWindow.IsVisible || textWindow.Editor.Text != updatedText) throw new Exception("取消关闭应保留文本修改");
        await RespondToTextClose(textWindow, "保存");
        await WaitAsync(() => !textWindow.IsVisible);
        string editedFile = Path.Combine(root, "文本", "编辑.json");
        if (await File.ReadAllTextAsync(editedFile) != updatedText) throw new Exception("关闭前保存内容未落盘");
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(item => item.Name == "编辑.json"));
        var discardWindow = client.OwnedWindows.OfType<TextViewer>().Single();
        discardWindow.Editor.Text = "放弃这次修改";
        await RespondToTextClose(discardWindow, "放弃");
        if (discardWindow.IsVisible || await File.ReadAllTextAsync(editedFile) != updatedText)
            throw new Exception("放弃修改应关闭窗口并保留服务端原内容");
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(item => item.Name == "编辑.json"));
        var conflictWindow = client.OwnedWindows.OfType<TextViewer>().Single();
        const string localDraft = "{\"值\":3}";
        const string externalEdit = "{\"值\":4}";
        conflictWindow.Editor.Text = localDraft;
        await File.WriteAllTextAsync(editedFile, externalEdit);
        await RespondToTextClose(conflictWindow, "保存");
        await WaitAsync(() => FindTextBlocks(conflictWindow).Any(text => text.Text.StartsWith("保存失败")));
        if (!conflictWindow.IsVisible || conflictWindow.Editor.IsReadOnly || conflictWindow.Editor.Text != localDraft
            || await File.ReadAllTextAsync(editedFile) != externalEdit)
            throw new Exception("保存发生冲突时应保留本地编辑和服务端文件");
        await RespondToTextClose(conflictWindow, "放弃");
        Console.WriteLine("通过：文本状态栏、查找和换行，保存、放弃、取消及保存冲突处理");
        // 使用接近实际日志大小的文件验证等待提示和滚动。
        string lineSuffix = new('x', 310);
        string longText = string.Join("\n", Enumerable.Range(1, 25_000).Select(i => $"line {i} {lineSuffix}"));
        await File.WriteAllTextAsync(Path.Combine(root, "文本", "long.txt"), longText);
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "文本"));
        copyPath.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Clipboard.GetText() != expectedPath + "/文本") throw new Exception("进入子目录后复制的路径未更新");
        Clipboard.Clear();
        var textBusyIndicator = (Border)browser.FindName("BusyIndicator");
        var textBusyMessage = (TextBlock)browser.FindName("BusyText");
        var busyMessageProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        bool showedBeforeViewer = false;
        void ObserveTextLoading(object? sender, EventArgs args)
        {
            if (textBusyMessage.Text != "正在准备文本查看器…") return;
            _ = browser.Dispatcher.InvokeAsync(() =>
            {
                showedBeforeViewer = textBusyIndicator.IsVisible && !client.OwnedWindows.OfType<TextViewer>().Any();
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
        busyMessageProperty.AddValueChanged(textBusyMessage, ObserveTextLoading);
        try
        {
            await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "long.txt"));
        }
        finally { busyMessageProperty.RemoveValueChanged(textBusyMessage, ObserveTextLoading); }
        if (!showedBeforeViewer || textBusyIndicator.IsVisible)
            throw new Exception("打开大文本时应先显示等待提示，查看器打开后应隐藏");
        Console.WriteLine("通过：打开大文本时等待提示先于查看器显示，打开后隐藏");
        var longWindow = client.OwnedWindows.OfType<TextViewer>().Last();
        await Task.Delay(500);
        var longScroll = FindDescendant<ScrollViewer>(longWindow.Editor) ?? throw new Exception("缺少文本滚动视图");
        double beforeWheel = longScroll.VerticalOffset;
        longWindow.Editor.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        await Task.Delay(400);
        double afterWheel = longScroll.VerticalOffset;
        Console.WriteLine($"滚轮步长：{beforeWheel} -> {afterWheel}");
        if (afterWheel - beforeWheel < 10) throw new Exception("滚轮滚动距离过小：" + (afterWheel - beforeWheel));
        longWindow.Close();
        grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(client), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => grid.Items.Count == 4 && ((Button)client.FindName("ConnectButton")).IsEnabled);
        Console.WriteLine("通过：文件列表退格键返回上一级");
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        var images = (await new RemoteFileClient("127.0.0.1", port).CallAsync(new("list"))).Entries!;
        grid.SelectedItem = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "early.png");
        await browser.OpenAsync((RemoteEntry)grid.SelectedItem);
        var imageWindow = client.OwnedWindows.OfType<ImageViewer>().Single();
        await WaitAsync(() => FindImage(imageWindow)?.Source != null);
        CheckImageName(imageWindow, "early.png");
        var previousArrow = FindButton(imageWindow, "上一张图片") ?? throw new Exception("缺少左侧图片切换箭头");
        var nextArrow = FindButton(imageWindow, "下一张图片") ?? throw new Exception("缺少右侧图片切换箭头");
        if (FindButtonByContent(imageWindow, "← 上一张") is not null || FindButtonByContent(imageWindow, "下一张 →") is not null)
            throw new Exception("不应保留上一张/下一张文本按钮");
        if (previousArrow.IsEnabled || !nextArrow.IsEnabled) throw new Exception("首张图片时箭头状态错误");
        // 箭头默认隐藏；指针进入图片区域后变淡出现，离开约 1 秒后淡出；悬停 0.8 秒浮出“下一张”提示。
        if (nextArrow.Opacity > 0.05) throw new Exception("箭头默认应隐藏");
        var stageField = typeof(ImageViewer).GetField("stage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new Exception("找不到图片舞台");
        var stageElement = (Grid)stageField.GetValue(imageWindow)!;
        stageElement.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
        await WaitAsync(() => nextArrow.Opacity > 0.3);
        stageElement.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
        await WaitAsync(() => nextArrow.Opacity < 0.05);
        nextArrow.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
        var arrowCaption = FindTextBlock(imageWindow, "下一张") ?? throw new Exception("缺少“下一张”悬停提示文字");
        await WaitAsync(() => arrowCaption.Parent is Border { Opacity: > 0.5 });
        nextArrow.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
        Console.WriteLine("通过：图片切换箭头靠近显示、悬停提示与离开淡出");
        var thumbList = FindDescendant<ListBox>(imageWindow) ?? throw new Exception("缺少缩略图条");
        if (thumbList.Items.Count != 2) throw new Exception("缩略图数量错误：" + thumbList.Items.Count);
        if (thumbList.IsVisible) throw new Exception("默认不应显示缩略图条");
        var thumbToggle = FindButton(imageWindow, "显示缩略图") ?? throw new Exception("缺少缩略图开关按钮");
        thumbToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => thumbList.IsVisible);
        if (thumbToggle.ToolTip?.ToString() != "隐藏缩略图") throw new Exception("缩略图开关提示未更新");
        // 缩略图点击切换
        thumbList.SelectedIndex = 1;
        await WaitAsync(() => imageWindow.CurrentPath == "late.png" && !nextArrow.IsEnabled);
        thumbList.SelectedIndex = 0;
        await WaitAsync(() => imageWindow.CurrentPath == "early.png" && !previousArrow.IsEnabled);
        Console.WriteLine("通过：缩略图默认隐藏、展开与点击切换");
        // 右下角缩放控件
        var percentBox = FindDescendant<TextBox>(imageWindow) ?? throw new Exception("缺少缩放比例输入框");
        if (!percentBox.Text.EndsWith("%")) throw new Exception("缩放比例未显示百分比：" + percentBox.Text);
        var zoomIn = FindButton(imageWindow, "放大 (Ctrl++)") ?? throw new Exception("缺少放大按钮");
        var zoomOut = FindButton(imageWindow, "缩小 (Ctrl+-)") ?? throw new Exception("缺少缩小按钮");
        var zoomSlider = FindDescendant<Slider>(imageWindow) ?? throw new Exception("缺少缩放滑块");
        if (FindButton(imageWindow, "全屏 (F11)") is null) throw new Exception("缺少全屏按钮");
        string beforeZoom = percentBox.Text;
        zoomIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => percentBox.Text != beforeZoom);
        zoomOut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => percentBox.Text == beforeZoom);
        zoomSlider.Value = 200;
        await WaitAsync(() => percentBox.Text == "200%");
        Console.WriteLine("通过：缩放比例、缩放滑块与全屏按钮");
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
        await CheckViewerMinimizeAsync(imageWindow);
        await imageWindow.MoveAsync(1);
        string lastViewed = imageWindow.CurrentPath;
        imageWindow.Close();
        await FlushAsync(grid);
        if (lastViewed != "late.png" || grid.SelectedItem is not RemoteEntry selectedImage || selectedImage.Path != lastViewed)
            throw new Exception("关闭图片查看器后应在文件列表选中最后查看的图片");
        Console.WriteLine("通过：图片真实解码，Right / Left 按创建时间前后切换");
        // 标注模式：三栏布局 + 左栏图片列表 + 已标注角标 + 左栏点击切换大图
        var annotationEntries = images.Where(e => e.IsImage).OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        var oracleTime = new DateTime(2026, 11, 23, 9, 24, 51, DateTimeKind.Unspecified);
        var utcTime = new DateTime(2026, 11, 22, 16, 12, 34, DateTimeKind.Utc);
        var existing = new RemoteAnnotation("", CategoryName: "外观异常", PlanName: "重新拍摄",
            Operator: "测试员", ImageName: annotationEntries[0].Name, UpdatedAt: oracleTime);
        string annotationPath = "DS616/A6-DPQG-044/SaveImages/" + new string('长', 140) + "/20261120/34";
        var annotationWindow = new ImageViewer(new RemoteFileClient("127.0.0.1", port), annotationEntries,
            annotationEntries[0].Path, new AnnotationContext("", annotationPath,
                [existing, existing with { Note = "旧标注", UpdatedAt = utcTime, Operator = "旧操作人" }, existing with { ImageName = null }]));
        Prepare(annotationWindow);
        annotationWindow.Show();
        await FlushAsync(annotationWindow);
        var listField = typeof(ImageViewer).GetField("annotationList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var leftList = (ListBox?)listField?.GetValue(annotationWindow) ?? throw new Exception("标注模式缺少左侧图片列表");
        if (leftList.Items.Count != annotationEntries.Length) throw new Exception("左栏图片数量错误：" + leftList.Items.Count);
        var firstItem = leftList.Items.OfType<ImageThumbItem>().First();
        if (firstItem.BadgeVisibility != Visibility.Visible || leftList.Items.OfType<ImageThumbItem>().Skip(1).Any(item => item.Annotated))
            throw new Exception("只有实际标注过的图片应显示角标");
        await WaitAsync(() => FindTextBlocks(annotationWindow).Any(block => block.Text == "已标注 2 次"));
        if (!FindTextBlocks(annotationWindow).Any(block => block.Text == annotationEntries[0].Name && block.Parent is StackPanel)
            || !FindTextBlocks(annotationWindow).Any(block => block.Text == "最近一次 · 外观异常 / 重新拍摄" && block.IsVisible))
            throw new Exception("当前图片名、标注次数和最近记录应分别显示");
        if (!FindTextBlocks(annotationWindow).Any(block => block.Text == $"已标注 1 / {annotationEntries.Length} 张"))
            throw new Exception("进度必须按已标注图片数计算");
        var historyButton = FindButton(annotationWindow, "查看标注历史") ?? throw new Exception("缺少图片历史入口");
        historyButton.ApplyTemplate();
        if (historyButton.FocusVisualStyle is not null
            || historyButton.Template.FindName("Chrome", historyButton) is not null)
            throw new Exception("已标注次数应使用不绘制焦点外框的按钮模板");
        annotationWindow.UpdateLayout();
        var historyIcon = ((StackPanel)historyButton.Content).Children.OfType<System.Windows.Shapes.Path>().Single();
        var pen = new Pen(historyIcon.Stroke, historyIcon.StrokeThickness)
        {
            LineJoin = historyIcon.StrokeLineJoin,
            StartLineCap = historyIcon.StrokeStartLineCap,
            EndLineCap = historyIcon.StrokeEndLineCap
        };
        var iconBounds = historyIcon.RenderedGeometry.GetRenderBounds(pen);
        var iconSlot = new Rect(historyIcon.RenderSize);
        iconSlot.Inflate(0.01, 0.01);
        if (iconBounds.IsEmpty || !iconSlot.Contains(iconBounds))
            throw new Exception($"标注历史箭头必须完整显示在图标范围内。描边 {iconBounds}，布局 {iconSlot}");
        historyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (historyIcon.RenderTransform is not RotateTransform { Angle: 180 })
            throw new Exception("展开标注历史后箭头应朝上");
        const System.Reflection.BindingFlags viewerFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var historyPanel = (StackPanel)typeof(ImageViewer).GetField("annotationHistory", viewerFields)!.GetValue(annotationWindow)!;
        var historyRows = historyPanel.Children.OfType<Border>().Select(record => (Grid)record.Child).ToArray();
        if (!historyPanel.IsVisible || historyRows.Length != 2)
            throw new Exception("历史应逐条显示当前图片的有效标注");
        static void CheckHistoryRecord(Grid fields, params (string Label, string Value)[] expected)
        {
            if (fields.RowDefinitions.Count != expected.Length) throw new Exception("标注历史字段行数不对");
            var textBlocks = fields.Children.OfType<TextBlock>().ToArray();
            for (int row = 0; row < expected.Length; row++)
            {
                var (label, value) = expected[row];
                if (!textBlocks.Any(text => Grid.GetRow(text) == row && Grid.GetColumn(text) == 0 && text.Text == label)
                    || !textBlocks.Any(text => Grid.GetRow(text) == row && Grid.GetColumn(text) == 1 && text.Text == value))
                    throw new Exception($"历史字段「{label}」应与值「{value}」在同一行");
            }
        }
        CheckHistoryRecord(historyRows[0], ("时间", "2026.11.23 09:24:51"), ("操作人", "测试员"),
            ("归类", "外观异常"), ("处理方案", "重新拍摄"));
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);
        CheckHistoryRecord(historyRows[1], ("时间", $"{localTime:yyyy.MM.dd HH:mm:ss}"), ("操作人", "旧操作人"),
            ("归类", "外观异常"), ("处理方案", "重新拍摄"), ("备注", "旧标注"));
        CheckAnnotationTitle(annotationWindow, annotationEntries[0].Name, annotationPath);
        if (ScrollViewer.GetHorizontalScrollBarVisibility(leftList) != ScrollBarVisibility.Disabled)
            throw new Exception("标注缩略图列表不应横向滚动");
        var caption = FindTextBlocks(leftList).First(block => block.Text == firstItem.Caption);
        if (caption.Text != $"01_{annotationEntries[0].Name}") throw new Exception("图片编号应使用下划线连接文件名");
        if (((SolidColorBrush)caption.Foreground).Color != ((SolidColorBrush)annotationWindow.FindResource("SuccessBrush")).Color)
            throw new Exception("已标注图片的文件名应为绿色");
        var note = (TextBox)typeof(ImageViewer).GetField("noteBox", viewerFields)!.GetValue(annotationWindow)!;
        var preview = (Grid)typeof(ImageViewer).GetField("stage", viewerFields)!.GetValue(annotationWindow)!;
        var source = PresentationSource.FromVisual(annotationWindow);
        var rightInNote = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        note.RaiseEvent(rightInNote);
        if (rightInNote.Handled || annotationWindow.CurrentPath != annotationEntries[0].Path)
            throw new Exception("备注框内的方向键不应被拦截或切换图片");
        preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitAsync(() => annotationWindow.CurrentPath == annotationEntries[1].Path);
        leftList.SelectedIndex = 0;
        await WaitAsync(() => annotationWindow.CurrentPath == annotationEntries[0].Path);
        var toggle = FindButton(annotationWindow, "折叠图片列表") ?? throw new Exception("左栏缺少折叠按钮");
        double previewWidth = preview.ActualWidth;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        annotationWindow.UpdateLayout();
        if (leftList.IsVisible || preview.ActualWidth <= previewWidth || FindButton(annotationWindow, "展开图片列表") != toggle)
            throw new Exception("折叠后应让出预览空间并保留展开入口");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        annotationWindow.UpdateLayout();
        if (!leftList.IsVisible || leftList.SelectedIndex != 0) throw new Exception("展开后应恢复原图片选择");

        var columns = (Grid)((Border)((DockPanel)leftList.Parent).Parent).Parent;
        var splitter = columns.Children.OfType<GridSplitter>().Single();
        splitter.ApplyTemplate();
        if (splitter.Template.FindName("Line", splitter) is not Border { Background: SolidColorBrush { Color.A: 0 } })
            throw new Exception("标注侧栏的分隔线在未操作时应隐藏");
        var form = (Border)columns.Children.Cast<UIElement>().Single(child => Grid.GetColumn(child) > Grid.GetColumn(splitter));
        double formWidth = form.ActualWidth;
        splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Left) { RoutedEvent = Keyboard.KeyDownEvent });
        annotationWindow.UpdateLayout();
        if (form.ActualWidth <= formWidth) throw new Exception("右栏分隔条应能调宽表单");
        annotationWindow.Height = annotationWindow.MinHeight;
        annotationWindow.Width = annotationWindow.MinWidth;
        annotationWindow.UpdateLayout();
        var submitAction = FindButtonByContent(annotationWindow, "新增标注") ?? throw new Exception("找不到新增标注按钮");
        var revokeAction = FindButtonByContent(annotationWindow, "撤销标注") ?? throw new Exception("找不到撤销标注按钮");
        var formLayout = (DockPanel)form.Child;
        var formScroll = formLayout.Children.OfType<ScrollViewer>().Single();
        formScroll.ScrollToTop();
        annotationWindow.UpdateLayout();
        var actionPosition = submitAction.TranslatePoint(new Point(0, 0), form);
        formScroll.ScrollToBottom();
        annotationWindow.UpdateLayout();
        if (formScroll.VerticalOffset <= 0 || !revokeAction.IsVisible || submitAction.TranslatePoint(new Point(0, 0), form) != actionPosition
            || actionPosition.Y + submitAction.ActualHeight > form.ActualHeight)
            throw new Exception("窄窗口滚动表单时，提交和撤销按钮应固定且可见");
        leftList.SelectedIndex = 1;
        await WaitAsync(() => annotationWindow.CurrentPath == annotationEntries[1].Path);
        await WaitAsync(() => FindTextBlocks(annotationWindow).Any(block => block.Text == "未标注"));
        if (!FindTextBlocks(annotationWindow).Any(block => block.Text == annotationEntries[1].Name && block.Parent is StackPanel)
            || FindTextBlocks(annotationWindow).Any(block => block.Text.StartsWith("最近一次", StringComparison.Ordinal) && block.IsVisible))
            throw new Exception("切到未标注图片时应更新文件名并隐藏上一张的标注摘要");
        annotationWindow.Width = annotationWindow.MinWidth;
        CheckAnnotationTitle(annotationWindow, annotationEntries[1].Name, annotationPath);
        leftList.SelectedIndex = 0;
        await WaitAsync(() => annotationWindow.CurrentPath == annotationEntries[0].Path);
        var filter = ((DockPanel)leftList.Parent).Children.OfType<CheckBox>().Single();
        filter.IsChecked = true;
        filter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await WaitAsync(() => leftList.Items.Count == annotationEntries.Length - 1 && annotationWindow.CurrentPath != annotationEntries[0].Path);
        await annotationWindow.MoveAsync(-1);
        if (annotationWindow.CurrentPath == annotationEntries[0].Path) throw new Exception("筛选时上一张应跳过已标注图片");
        filter.IsChecked = false;
        filter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        if (leftList.Items.Count != annotationEntries.Length) throw new Exception("清除筛选后应显示所有图片");
        note.Text = "未提交的备注";
        void AnswerDraftConfirmation(bool discard)
        {
            _ = annotationWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = annotationWindow.OwnedWindows.OfType<Window>().SingleOrDefault(window => window.Title == "未提交的标注");
                if (dialog is not null) dialog.DialogResult = discard;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        AnswerDraftConfirmation(false);
        annotationWindow.Close();
        if (!annotationWindow.IsVisible || note.Text != "未提交的备注")
            throw new Exception("取消关窗时应保留未提交草稿");
        AnswerDraftConfirmation(true);
        annotationWindow.Close();
        if (annotationWindow.IsVisible) throw new Exception("确认放弃后应关闭标注窗口");
        Console.WriteLine("通过：标注窗口布局、方向键隔离、标注进度、历史和未标注筛选");
        // 大图默认适应窗口，实际大小按钮应能来回切换而不是点一次就禁用。
        var bigBitmap = BitmapSource.Create(2000, 1500, 96, 96, PixelFormats.Bgra32, null, new byte[2000 * 1500 * 4], 2000 * 4);
        var bigEncoder = new PngBitmapEncoder();
        bigEncoder.Frames.Add(BitmapFrame.Create(bigBitmap));
        using (var bigFile = File.Create(Path.Combine(root, "zoom.png"))) bigEncoder.Save(bigFile);
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "zoom.png"));
        var zoomWindow = client.OwnedWindows.OfType<ImageViewer>().Last();
        await WaitAsync(() => FindImage(zoomWindow)?.Source != null);
        var zoomPercent = FindDescendant<TextBox>(zoomWindow) ?? throw new Exception("缺少缩放比例输入框");
        string fitPercent = zoomPercent.Text;
        if (fitPercent == "100%") throw new Exception("大图初始应显示适应窗口比例");
        var actualButton = FindButton(zoomWindow, "缩放到实际大小 (100%)") ?? throw new Exception("缺少实际大小按钮");
        actualButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => zoomPercent.Text == "100%");
        var fitButton = FindButton(zoomWindow, "适应窗口") ?? throw new Exception("100% 时应能切回适应窗口");
        fitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => zoomPercent.Text == fitPercent);
        // 滚轮缩小到适应窗口就停住；减号可以继续缩小。
        var previewCanvas = FindDescendant<Canvas>(zoomWindow) ?? throw new Exception("缺少图片画布");
        previewCanvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        await FlushAsync(zoomWindow);
        if (zoomPercent.Text != fitPercent) throw new Exception("滚轮缩小不应低于适应窗口比例");
        var zoomOutButton = FindButton(zoomWindow, "缩小 (Ctrl+-)") ?? throw new Exception("缺少缩小按钮");
        zoomOutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => zoomPercent.Text != fitPercent);
        string belowFit = zoomPercent.Text;
        previewCanvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        await FlushAsync(zoomWindow);
        if (zoomPercent.Text != belowFit) throw new Exception("低于适应比例后滚轮不应继续缩小");
        zoomWindow.Close();
        Console.WriteLine("通过：实际大小与适应窗口可来回切换");
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
        var thumbStrip = FindDescendant<ListBox>(thumbWindow) ?? throw new Exception("缺少缩略图条");
        if (thumbStrip.Items.Count != 25) throw new Exception("缩略图数量错误：" + thumbStrip.Items.Count);
        var stripToggle = FindButton(thumbWindow, "显示缩略图") ?? throw new Exception("缺少缩略图开关按钮");
        stripToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => thumbStrip.IsVisible);
        await FlushAsync(thumbStrip);
        var thumbScroll = FindDescendant<ScrollViewer>(thumbStrip) ?? throw new Exception("缩略图条缺少滚动视图");
        thumbStrip.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        await WaitAsync(() => thumbScroll.HorizontalOffset > 0);
        await Task.Delay(300);
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
        scrollNode.IsExpanded = true;
        await WaitAsync(() => scrollNode.Items.Count > 50);
        await FlushAsync(tree);
        var directoryScroll = FindDescendant<ScrollViewer>(tree) ?? throw new Exception("缺少目录树滚动容器");
        var directoryBar = FindDescendant<System.Windows.Controls.Primitives.ScrollBar>(directoryScroll)
            ?? throw new Exception("缺少目录树滚动条");
        Console.WriteLine($"目录滚动状态：extent={directoryScroll.ExtentHeight}, viewport={directoryScroll.ViewportHeight}, scrollable={directoryScroll.ScrollableHeight}, bar={directoryBar.Visibility}, height={directoryBar.ActualHeight}");
        if (directoryScroll.ScrollableHeight <= 0 || !directoryBar.IsVisible)
            throw new Exception("目录内容超过窗口高度后必须显示滚动条");
        directoryScroll.ScrollToBottom();
        await FlushAsync(tree);
        if (directoryScroll.VerticalOffset <= 0) throw new Exception("目录树滚动条无法滚动");
        directoryScroll.ScrollToTop();
        await FlushAsync(tree);
        directoryScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.MouseWheelEvent });
        await FlushAsync(tree);
        if (directoryScroll.VerticalOffset <= 0) throw new Exception("目录树滚轮无法滚动");
        var directoryTrack = (System.Windows.Controls.Primitives.Track)directoryBar.Template.FindName("PART_Track", directoryBar);
        directoryTrack.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 10000)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        await FlushAsync(tree);
        if (Math.Abs(directoryScroll.VerticalOffset - directoryScroll.ScrollableHeight) > 1)
            throw new Exception("目录树拖动滚动条无法到达底部");
        Console.WriteLine("通过：展开大量子目录后显示滚动条，滚轮和滑块滚动生效");
        scrollNode.IsSelected = true;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count > 1);
        await FlushAsync(browser);
        await WaitAsync(() => scroll.VerticalOffset == 0);
        Console.WriteLine("通过：切换目录和返回上一级后文件列表回到顶部");
        scrollNode.ApplyTemplate();
        var selectedRow = (Border)scrollNode.Template.FindName("Bd", scrollNode);
        var treeArrow = (FrameworkElement)scrollNode.Template.FindName("Expander", scrollNode);
        var arrowPosition = treeArrow.TransformToAncestor(selectedRow).Transform(new Point());
        if (Grid.GetColumnSpan(selectedRow) != 2 || arrowPosition.X < 0
            || arrowPosition.X + treeArrow.ActualWidth > selectedRow.ActualWidth
            || selectedRow.CornerRadius.TopLeft != 4)
            throw new Exception("目录选中背景应包含展开箭头并使用圆角");
        var overviewScroll = (ScrollViewer)browser.FindName("NgOverviewScroll");
        if (overviewScroll.FocusVisualStyle is not null || !overviewScroll.Focusable)
            throw new Exception("进度容器应隐藏外围焦点虚线并保留键盘滚动能力");
        var browserScrollBar = FindDescendant<System.Windows.Controls.Primitives.ScrollBar>(grid)
            ?? throw new Exception("缺少资源管理器滚动条");
        browserScrollBar.ApplyTemplate();
        var browserTrack = (System.Windows.Controls.Primitives.Track)browserScrollBar.Template.FindName("PART_Track", browserScrollBar);
        browserTrack.Thumb.ApplyTemplate();
        var thumbBody = browserTrack.Thumb.Template.FindName("ThumbBody", browserTrack.Thumb) as Border;
        if (thumbBody is null || thumbBody.Opacity >= 1 || browserScrollBar.Width != 12)
            throw new Exception("运行时未应用资源管理器细滑块模板");
        Console.WriteLine($"滑块宽度：列表设定={thumbBody.Width}, 实际={thumbBody.ActualWidth}, 目录设定={((Border)directoryTrack.Thumb.Template.FindName("ThumbBody", directoryTrack.Thumb)).Width}");
        if (thumbBody.Width is not (3 or 5))
            throw new Exception("资源管理器滚动滑块可见宽度应为 3 到 5 个逻辑像素");
        var directoryThumbBody = directoryTrack.Thumb.Template.FindName("ThumbBody", directoryTrack.Thumb) as Border;
        if (directoryThumbBody is null || directoryThumbBody.Width is not (3 or 5))
            throw new Exception("目录树与文件列表应使用相同的细滑块宽度");
        Console.WriteLine("通过：运行时目录高亮覆盖箭头，进度容器无外围焦点框，滚动条使用细滑块模板");
        // 树节点点击切回上级后，树的选中项必须停在当前目录，否则左右两边会指向不同位置。
        if (SelectedTreeEntry(tree) is not { } followedBack || followedBack.Path != "滚动测试")
            throw new Exception("点击树节点切回上级后，树选中项未跟随当前目录：" + (SelectedTreeEntry(tree)?.Path ?? "(无选中)"));
        var bigFolder = grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "大目录");
        await browser.OpenAsync(bigFolder);
        await FlushAsync(browser);
        // 通过列表进入尚未展开的子目录时，树要逐级展开并选中该目录。
        if (SelectedTreeEntry(tree) is not { } followedDeep || followedDeep.Path != "滚动测试/大目录")
            throw new Exception("进入子目录后，树选中项未跟随当前目录：" + (SelectedTreeEntry(tree)?.Path ?? "(无选中)"));
        Console.WriteLine("通过：列表导航后目录树逐级展开并跟随当前目录");
        grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(client), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        try { await WaitAsync(() => !browser.IsBusy && grid.SelectedItem is RemoteEntry { Name: "大目录" }); }
        catch (OperationCanceledException)
        {
            throw new Exception($"返回上一级未选中大目录；busy={browser.IsBusy}，selected={(grid.SelectedItem as RemoteEntry)?.Name ?? "(null)"}，items={grid.Items.Count}");
        }
        await FlushAsync(browser);
        grid.UpdateLayout();
        var backScroll = FindScrollViewer(grid) ?? throw new Exception("未找到文件列表滚动条");
        if (backScroll.VerticalOffset <= 0) throw new Exception("返回上一级后未定位到刚才的文件夹");
        Console.WriteLine("通过：返回上一级定位到刚才所在文件夹");
        string pageRoot = Path.Combine(root, "分页测试");
        Directory.CreateDirectory(pageRoot);
        for (int i = 2; i < 620; i++) await File.WriteAllTextAsync(Path.Combine(pageRoot, i + ".txt"), "");
        await File.WriteAllBytesAsync(Path.Combine(pageRoot, "1.png"), sampleImage);
        await File.WriteAllBytesAsync(Path.Combine(pageRoot, "620.png"), sampleImage);
        File.SetCreationTimeUtc(Path.Combine(pageRoot, "1.png"), DateTime.UtcNow.AddDays(-1));
        await browser.ConnectAsync(new RemoteFileClient("127.0.0.1", port));
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "分页测试"));
        await FlushAsync(browser);
        if (grid.Items.Count != 500) throw new Exception("首屏未按 500 条分页加载：" + grid.Items.Count);
        if (selectionText.Text != "620 项") throw new Exception("分页目录应显示完整总数：" + selectionText.Text);
        var selectAll = typeof(ClientBrowser).GetMethod("SelectAllAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new Exception("找不到全选方法");
        await (Task)selectAll.Invoke(browser, null)!;
        await WaitAsync(() => grid.SelectedItems.Count == 620 && grid.Items.Count == 620);
        grid.SelectedItems.Clear();
        Console.WriteLine("通过：Ctrl+A 会先加载全部条目再全选");
        ((TreeViewItem)tree.Items[0]).IsSelected = true;
        await WaitAsync(() => !browser.IsBusy && grid.Items.Count > 1);
        await browser.OpenAsync(grid.Items.Cast<RemoteEntry>().Single(e => e.Name == "分页测试"));
        await FlushAsync(browser);
        if (grid.Items.Count != 500) throw new Exception("滚动前重新进入目录应只加载分页首屏");
        var pageScroll = FindScrollViewer(grid) ?? throw new Exception("未找到文件列表滚动条");
        if (pageScroll.VerticalOffset != 0) throw new Exception($"重新进入分页目录必须从顶部开始，实际偏移 {pageScroll.VerticalOffset}");
        pageScroll.ScrollToBottom();
        await WaitAsync(() => grid.Items.Count == 620);
        if (selectionText.Text != "620 项") throw new Exception("滚动加载完成后计数错误：" + selectionText.Text);
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
        var parent = FindButton(browser, "返回上一级目录") ?? throw new Exception("找不到上一级目录按钮");
        parent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !browser.IsBusy && grid.SelectedItem is RemoteEntry { Name: "分页测试" });
        await browser.OpenAsync((RemoteEntry)grid.SelectedItem);
        await FlushAsync(grid);
        if (grid.Items.Cast<RemoteEntry>().Any(item => item.Name == "620.png")) throw new Exception("目标图片应尚未载入列表");
        var firstImage = grid.Items.Cast<RemoteEntry>().Single(item => item.Name == "1.png");
        grid.SelectedItem = firstImage;
        grid.ScrollIntoView(firstImage);
        grid.UpdateLayout();
        var firstImageRow = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(firstImage)
            ?? throw new Exception("找不到用于双击的图片行");
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = firstImageRow });
        await WaitAsync(() => !browser.IsBusy && client.OwnedWindows.OfType<ImageViewer>().Any());
        var pagedViewer = client.OwnedWindows.OfType<ImageViewer>().Single();
        await WaitAsync(() => FindButton(pagedViewer, "下一张图片") is { IsEnabled: true });
        (FindButton(pagedViewer, "下一张图片")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => pagedViewer.CurrentPath == "分页测试/620.png");
        pagedViewer.Close();
        await WaitAsync(() => grid.SelectedItem is RemoteEntry { Name: "620.png" } && pageScroll.VerticalOffset > 0);
        await browser.OpenAsync((RemoteEntry)grid.SelectedItem);
        pagedViewer = client.OwnedWindows.OfType<ImageViewer>().Single();
        parent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !browser.IsBusy && grid.SelectedItem is RemoteEntry { Name: "分页测试" });
        pagedViewer.Close();
        await FlushAsync(grid);
        if (grid.SelectedItem is not RemoteEntry { Name: "分页测试" }) throw new Exception("切换目录后关窗不应改变当前列表选择");
        Console.WriteLine("通过：关窗后自动加载分页，选中并滚动到最后查看的图片");
        var execute = typeof(ClientBrowser).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(Func<CancellationToken, Task>), typeof(bool) }, null) ?? throw new Exception("找不到可取消操作入口");
        var busyIndicator = (Border)browser.FindName("BusyIndicator");
        bool fastIndicatorShown = false;
        DependencyPropertyChangedEventHandler watchIndicator = (_, e) => fastIndicatorShown |= e.NewValue is true;
        busyIndicator.IsVisibleChanged += watchIndicator;
        try
        {
            await (Task)execute.Invoke(browser, [new Func<CancellationToken, Task>(async _ => await Task.Yield()), false])!;
            await Task.Delay(300);
        }
        finally { busyIndicator.IsVisibleChanged -= watchIndicator; }
        if (fastIndicatorShown || busyIndicator.IsVisible) throw new Exception("短操作结束后不应闪现等待提示");
        var slow = (Task)execute.Invoke(browser, new object[]
        {
            new Func<CancellationToken, Task>(async token => await Task.Delay(5000, token)), true
        })!;
        var cancelButton = (Button)browser.FindName("CancelButton");
        if (busyIndicator.IsVisible) throw new Exception("慢操作开始时应先延迟显示等待提示");
        await WaitAsync(() => cancelButton.IsVisible);
        tree.UpdateLayout();
        var busyTreeRoot = (TreeViewItem)tree.Items[0];
        var busyTreeChild = busyTreeRoot.Items.OfType<TreeViewItem>().First();
        if (tree.IsEnabled || busyTreeRoot.Opacity != 1 || busyTreeChild.Opacity != 1)
            throw new Exception("操作期间目录树应暂停交互，父子节点应保持正常透明度");
        var treeSurface = tree.Template.FindName("TreeSurface", tree) as Border;
        if (treeSurface is null || !Equals(treeSurface.Background, tree.Background))
            throw new Exception("操作期间目录树应保留原底色");
        if (busyIndicator.HorizontalAlignment != HorizontalAlignment.Right || busyIndicator.VerticalAlignment != VerticalAlignment.Bottom)
            throw new Exception("慢操作应在文件区域右下角显示等待提示");
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await slow;
        if (((TextBlock)browser.FindName("StatusText")).Text != "已取消" || browser.IsBusy || busyIndicator.IsVisible || cancelButton.Visibility != Visibility.Collapsed)
            throw new Exception("取消后状态或界面未恢复");
        Console.WriteLine("通过：短操作不闪现等待提示，长操作延迟提示并可取消");
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
        // 静默更新提示：平时隐藏，服务端有更新时显示蓝色徽标。
        var updateBadge = (Button)client.FindName("UpdateBadge");
        if (updateBadge.Visibility != Visibility.Collapsed) throw new Exception("服务端没有更新时不应显示更新提示");
        string updateRoot = Path.Combine(temp, "remote-ui-updates");
        Directory.CreateDirectory(updateRoot);
        byte[] updateBytes = System.Text.Encoding.UTF8.GetBytes("update-package");
        await File.WriteAllBytesAsync(Path.Combine(updateRoot, "client.zip"), updateBytes);
        string updateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(updateBytes));
        string updateNotes = "第一行更新说明\r\n" + new string('长', 200) + "\n最后一行";
        await File.WriteAllTextAsync(Path.Combine(updateRoot, "manifest.json"),
            JsonSerializer.Serialize(new { version = "9.9.9", file = "client.zip", sha256 = updateHash, notes = updateNotes }));
        await using (var updateService = new RemoteFileService(root) { UpdateDirectory = updateRoot })
        {
            var updateProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            updateProbe.Start(); int updatePort = ((System.Net.IPEndPoint)updateProbe.LocalEndpoint).Port; updateProbe.Stop();
            updateService.Start(updatePort);
            var updateClient = new ClientWindow(Path.Combine(temp, "remote-client-update.json")); Prepare(updateClient); updateClient.Show();
            ((ComboBox)updateClient.FindName("EndpointBox")).Text = $"127.0.0.1:{updatePort}";
            Click(updateClient, "ConnectButton");
            await WaitAsync(() => ((Button)updateClient.FindName("UpdateBadge")).Visibility == Visibility.Visible);
            var shownBadge = (Button)updateClient.FindName("UpdateBadge");
            if (shownBadge.ToolTip is not ToolTip { Content: TextBlock updateText } tip
                || !updateText.Text.Contains("9.9.9") || !updateText.Text.EndsWith(updateNotes, StringComparison.Ordinal)
                || updateText.TextWrapping != TextWrapping.Wrap)
                throw new Exception("更新提示应显示版本、保留说明的原始换行并自动折行");
            updateText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var popupSize = new Size(updateText.DesiredSize.Width + 14, updateText.DesiredSize.Height + 10);
            var targetSize = new Size(shownBadge.ActualWidth, shownBadge.ActualHeight);
            if (updateText.DesiredSize.Width > 400 || tip.CustomPopupPlacementCallback?.Invoke(popupSize, targetSize, default).First().Point.X + popupSize.Width != targetSize.Width)
                throw new Exception("更新提示应限制宽度，并将右边缘对齐更新按钮");
            if (shownBadge.Content is not System.Windows.Shapes.Path) throw new Exception("更新提示应使用图标而不是文字");
            await CloseAsync(updateClient);
        }
        Console.WriteLine("通过：静默检查更新并在有新版本时显示蓝色提示");
        // 夜间模式：图标按钮切换主题，颜色立即变化，再切回。
        var themeButton = (Button)client.FindName("ThemeButton");
        if (themeButton.Content is not System.Windows.Shapes.Path) throw new Exception("夜间模式按钮应使用图标");
        var lightCanvas = ((SolidColorBrush)client.FindResource("CanvasBrush")).Color;
        if (((System.Windows.Shapes.Path)client.FindName("ThemeIcon")).Stroke is null) throw new Exception("夜间模式图标未设置描边颜色");
        themeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(client);
        var darkCanvas = ((SolidColorBrush)client.Background).Color;
        if (darkCanvas == lightCanvas) throw new Exception("切换夜间模式后背景色未变化");
        if (client.Background is not SolidColorBrush darkBackground || darkBackground.Color != darkCanvas)
            throw new Exception("窗口背景未跟随夜间模式刷新");
        if (((System.Windows.Shapes.Path)client.FindName("ThemeIcon")).Data is null) throw new Exception("夜间模式图标未设置");
        themeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(client);
        if (((SolidColorBrush)client.Background).Color != lightCanvas) throw new Exception("切回日间模式后背景色未恢复");
        Console.WriteLine("通过：夜间模式图标按钮切换即时生效并可恢复");
        int rootCount = Directory.GetFileSystemEntries(root).Length;
        await CloseAsync(client);
        Click(server, "StopButton");
        await WaitAsync(() => ((Button)server.FindName("StartButton")).IsEnabled);
        Click(server, "StartButton");
        await WaitAsync(() => ((DataGrid)serverBrowser.FindName("FileGrid")).Items.Count == rootCount);
        await CloseAsync(server);
        Console.WriteLine("通过：新文件管理服务停止、重新启动与窗口关闭");
    }
    private static async Task CheckAnnotationReopenAsync(string temp)
    {
        const string dateFolder = "A601/20261120";
        const string batch = dateFolder + "/1001";
        string root = Path.Combine(temp, "annotation-ui");
        for (int i = 1000; i < 1021; i++) Directory.CreateDirectory(Path.Combine(root, dateFolder, i.ToString()));
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(root, batch, "image.png"))) encoder.Save(output);
        File.Copy(Path.Combine(root, batch, "image.png"), Path.Combine(root, batch, "other.png"));
        File.Copy(Path.Combine(root, batch, "image.png"), Path.Combine(root, dateFolder, "1004", "image.png"));

        string repositoryPath = Path.Combine(temp, "annotation-ui.json");
        await File.WriteAllTextAsync(repositoryPath, JsonSerializer.Serialize(new
        {
            dictionary = new RemoteDictionary([new("category", "测试归类")], [new("plan", "测试方案")]),
            annotations = Array.Empty<RemoteAnnotation>()
        }));
        var annotationRepository = new JsonAnnotationRepository(repositoryPath);
        await annotationRepository.SaveAsync(new RemoteAnnotation(dateFolder + "/1000", "A601",
            AutoLineDate: new DateTime(2026, 11, 20), BatchNumber: "1000", CategoryName: "测试归类",
            Operator: "测试员", UpdatedAt: new DateTime(2026, 11, 23, 9, 24, 51, DateTimeKind.Unspecified), ImageName: "sample.png"));
        await using var service = new RemoteFileService(root, annotationRepository)
        {
            ListFilter = (_, _, entries) => Task.FromResult(new RemoteFilterResult(entries)),
            NgBatchQuery = (_, _, _, _) => Task.FromResult<RemoteBatchKey[]>(
                [new(new DateTime(2026, 11, 20), "1001"), new(new DateTime(2026, 11, 20), "1004")])
        };
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        var browser = new ClientBrowser();
        var host = new Window { Content = browser, Width = 900, Height = 600 };
        Prepare(host); host.Show();
        try
        {
            await browser.ConnectAsync(endpoint);
            var filters = (StackPanel)browser.FindName("FilterRightHost");
            void ToggleNg(bool enabled)
            {
                if (filters.Children.OfType<CheckBox>().SingleOrDefault() is { } ngFilter) ngFilter.IsChecked = enabled;
                else ((Action<string?>)typeof(ClientBrowser).GetMethod("SetLocalFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .CreateDelegate(typeof(Action<string?>), browser))(enabled ? "ng" : null);
            }
            ToggleNg(true);
            await WaitAsync(() => !browser.IsBusy);
            var dateEntry = (await endpoint.CallAsync(new("list", "A601"))).Entries!.Single();
            await browser.OpenAsync(dateEntry);
            var grid = (DataGrid)browser.FindName("FileGrid");
            if (grid.SelectionMode != DataGridSelectionMode.Single) throw new Exception("日期目录应限制为单选");
            var existingTooltip = grid.Items.Cast<RemoteEntry>().Single(item => item.Name == "1000").AnnotationTip;
            if (!existingTooltip.Contains("2026-11-23 09:24"))
                throw new Exception("列表的标注提示应保留无时区的 Oracle 时间：" + existingTooltip);
            grid.SelectedIndex = 0;
            var selectAll = typeof(ClientBrowser).GetMethod("SelectAllAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)selectAll.Invoke(browser, null)!;
            if (grid.SelectedItems.Count != 1) throw new Exception("日期目录按全选后应仍为单选");
            var entry = grid.Items.Cast<RemoteEntry>().Single(item => item.Path == batch);
            grid.UpdateLayout();
            var batchRow = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(entry)
                ?? throw new Exception("未找到日期目录的待标注批次行");
            if (batchRow.IsSelected) throw new Exception("右键测试需要从另一个批次切换");
            batchRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            if (!ReferenceEquals(grid.SelectedItem, entry) || grid.SelectedItems.Count != 1)
                throw new Exception("日期目录右键应只选中被点击的批次");
            OpenContext(batchRow);
            if (!batchRow.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "标注")))
                throw new Exception("日期目录右键应生成批次标注菜单");
            var trackedEntries = new HashSet<RemoteEntry> { entry };
            var annotate = (Button)browser.FindName("AnnotateButton");
            CheckRoundedButtonFocus(annotate);
            if (!annotate.IsEnabled) throw new Exception("关闭标注窗口前，标注按钮应可用");
            Click(browser, "AnnotateButton");
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            if (annotate.IsEnabled) throw new Exception("标注窗口打开期间应禁用列表的标注按钮");
            var viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
            await browser.OpenAnnotationAsync(entry);
            if (host.OwnedWindows.OfType<ImageViewer>().Single() != viewer) throw new Exception("重复标注打开了另一个窗口");
            await WaitAsync(() => FindImage(viewer)?.Source is not null);
            CheckAnnotationInputs(viewer);
            await CheckViewerMinimizeAsync(viewer);
            if (!FindTextBlocks(viewer).Any(block => block.Text == $"annotation-ui/{batch}")) throw new Exception("顶栏未显示完整共享路径");
            CheckInlineAnnotationPath(viewer, $"annotation-ui/{batch}");
            if (FindButtonByContent(viewer, "撤销标注") is { IsVisible: true }) throw new Exception("未标注图片应隐藏撤销按钮");
            string imageName = Path.GetFileName(viewer.CurrentPath);
            var submit = FindButtonByContent(viewer, "提交标注") ?? throw new Exception("找不到提交标注按钮");
            CheckRoundedButtonFocus(submit);
            const System.Reflection.BindingFlags fieldFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void CheckMissingField(string name)
            {
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!viewer.IsVisible || annotate.IsEnabled) throw new Exception("表单验证失败后应保持标注窗口打开");
                if (FocusManager.GetFocusedElement(viewer) != typeof(ImageViewer).GetField(name, fieldFlags)!.GetValue(viewer))
                    throw new Exception("表单验证失败后应聚焦第一个缺失字段：" + name);
            }
            CheckMissingField("categoryPicker");
            SelectAnnotationOption(viewer, "categoryPicker");
            CheckMissingField("planPicker");
            SelectAnnotationOption(viewer, "planPicker");
            var operatorInput = (TextBox)typeof(ImageViewer).GetField("operatorBox", fieldFlags)!.GetValue(viewer)!;
            var operatorName = operatorInput.Text;
            operatorInput.Clear();
            CheckMissingField("operatorBox");
            operatorInput.Text = operatorName;
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible && entry.AnnotationStatus == "已标注" && annotate.IsEnabled);
            if (grid.SelectedItems.Count != 1 || !ReferenceEquals(grid.SelectedItem, entry) || !annotate.IsEnabled)
                throw new Exception("关闭标注窗口后，应保留原批次为唯一选中项，且标注按钮可用");
            if ((await endpoint.GetAnnotationAsync(entry.Path))?.CategoryName != "测试归类") throw new Exception("列表更新前应已保存标注");
            var nextEntry = grid.Items.Cast<RemoteEntry>().Single(item => item.Name == "1004");
            grid.ScrollIntoView(nextEntry);
            grid.UpdateLayout();
            var nextRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(nextEntry);
            nextRow.IsSelected = true;
            if (grid.SelectedItems.Count != 1 || !ReferenceEquals(grid.SelectedItem, nextEntry) || !annotate.IsEnabled)
                throw new Exception($"提交后切换到另一批次时应仍为单选且可以标注，实际选中 {grid.SelectedItems.Count} 项");
            if (!trackedEntries.Contains(entry)) throw new Exception("标注状态更新改变了原文件行在哈希集合中的身份");
            Click(browser, "AnnotateButton");
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            if (!host.OwnedWindows.OfType<ImageViewer>().Single().CurrentPath.StartsWith(nextEntry.Path + "/", StringComparison.Ordinal))
                throw new Exception("标注窗口应打开新选中的批次");
            host.OwnedWindows.OfType<ImageViewer>().Single().Close();
            await WaitAsync(() => annotate.IsEnabled);
            await browser.OpenAsync(entry);
            if (grid.SelectionMode != DataGridSelectionMode.Extended) throw new Exception("进入批次后应恢复多选");
            var annotatedImage = grid.Items.Cast<RemoteEntry>().Single(item => item.Name == imageName);
            var unannotatedImage = grid.Items.Cast<RemoteEntry>().Single(item => item.Name != imageName);
            if (annotatedImage.AnnotationStatus != "已标注" || unannotatedImage.AnnotationStatus != "未标注")
                throw new Exception("从日期层进入批次后应显示各图片自己的标注状态");
            grid.UpdateLayout();
            var annotatedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(annotatedImage);
            OpenContext(annotatedRow);
            if (!annotatedRow.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "标注")))
                throw new Exception("批次内图片右键菜单应显示标注");
            var imageLabel = FindTextBlocks(annotatedRow).Single(block => block.Text == annotatedImage.Name);
            if (((SolidColorBrush)imageLabel.Foreground).Color != ((SolidColorBrush)browser.FindResource("SuccessBrush")).Color)
                throw new Exception("批次文件列表的已标注图片名应为绿色");
            grid.SelectedItem = unannotatedImage;
            Click(browser, "AnnotateButton");
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
            if (viewer.CurrentPath != unannotatedImage.Path) throw new Exception("从图片行发起标注应打开所选图片");
            await WaitAsync(() => FindImage(viewer)?.Source is not null);
            SelectAnnotationOption(viewer, "categoryPicker");
            SelectAnnotationOption(viewer, "planPicker");
            FindButtonByContent(viewer, "提交标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible && unannotatedImage.AnnotationStatus == "已标注" && annotate.IsEnabled);
            var history = await endpoint.GetAnnotationHistoryAsync(entry.Path);
            if (history.Length != 2 || history.Select(item => item.ImageName).Distinct().Count() != 2)
                throw new Exception("从批次和图片入口提交后，应为两张图片分别保留记录");
            await endpoint.SaveAnnotationAsync(entry.Path, "测试归类", "测试方案", "再次标注", "测试员", unannotatedImage.Name);
            Click(browser, "AnnotateButton");
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
            await WaitAsync(() => FindImage(viewer)?.Source is not null);
            var allMarkedList = (ListBox)typeof(ImageViewer).GetField("annotationList", fieldFlags)!.GetValue(viewer)!;
            var filter = ((DockPanel)allMarkedList.Parent).Children.OfType<CheckBox>().Single();
            filter.IsChecked = true;
            filter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            if (allMarkedList.Items.Count != 0 || viewer.CurrentPath != unannotatedImage.Path
                || !FindTextBlocks(viewer).Any(block => block.IsVisible && block.Text == "本批次图片均已标注"))
                throw new Exception("筛选全部已标注的批次时应显示空状态并保留当前预览");
            if (FindButtonByContent(viewer, "撤销标注") is not { IsVisible: true } revoke)
                throw new Exception("已标注图片应显示撤销按钮");
            revoke.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible && unannotatedImage.AnnotationStatus == "未标注" && annotate.IsEnabled);
            if ((await endpoint.GetAnnotationHistoryAsync(entry.Path)).Single().ImageName != annotatedImage.Name)
                throw new Exception("撤销后只应保留另一张图片的记录");
            var annotationColumn = grid.Columns.Single(column => column.SortMemberPath == "AnnotationStatus");
            ToggleNg(false);
            await WaitAsync(() => !browser.IsBusy);
            if (annotationColumn.Visibility != Visibility.Collapsed || grid.Items.Cast<RemoteEntry>().Any(item => item.AnnotationStatus.Length != 0))
                throw new Exception("取消只看NG后应隐藏图片明细中的标注状态");
            ToggleNg(true);
            await WaitAsync(() => !browser.IsBusy);
            if (annotationColumn.Visibility != Visibility.Visible || grid.Items.Cast<RemoteEntry>().Single(item => item.Name == annotatedImage.Name).AnnotationStatus != "已标注")
                throw new Exception("重新勾选只看NG后应恢复图片明细中的标注状态");
            grid.SelectedItem = grid.Items.Cast<RemoteEntry>().Single(item => item.Name == unannotatedImage.Name);
            Click(browser, "AnnotateButton");
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
            await WaitAsync(() => FindImage(viewer)?.Source is not null);
            await AddAnnotationOptionAsync(viewer, "新增异常归类", "自定义归类");
            await AddAnnotationOptionAsync(viewer, "新增异常处理方案", "自定义处理方案");
            SelectAnnotationOption(viewer, "categoryPicker", "自定义归类");
            SelectAnnotationOption(viewer, "planPicker", "自定义处理方案");
            FindButtonByContent(viewer, "提交标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible && annotate.IsEnabled);
            if (!(await endpoint.GetAnnotationHistoryAsync(entry.Path)).Any(item => item.ImageName == unannotatedImage.Name
                && item.CategoryName == "自定义归类" && item.PlanName == "自定义处理方案"))
                throw new Exception("重新打开下拉框后应能选用并提交新字典项");
            var overview = (FrameworkElement)browser.FindName("NgOverviewPanel");
            ((TextBox)browser.FindName("NgStartDate")).Text = "2026-11-20";
            ((TextBox)browser.FindName("NgEndDate")).Text = "2026-11-20";
            var machine = (await endpoint.CallAsync(new("list"))).Entries!.Single(item => item.Name == "A601");
            await browser.OpenAsync(machine);
            var overviewDays = (ItemsControl)browser.FindName("NgOverviewDays");
            var pendingFilter = (RadioButton)browser.FindName("NgPendingFilter");
            void CheckFilterCounts(string all, string pendingCount, string done)
            {
                string? Count(string name) => (browser.FindName(name) as System.Windows.Documents.Run)?.Text.Trim();
                if (Count("NgAllCount") != all || Count("NgPendingCount") != pendingCount || Count("NgDoneCount") != done)
                    throw new Exception($"状态筛选应显示查询范围内的批次数量（全部 {all}、未标注 {pendingCount}、已标注 {done}）");
            }
            await WaitAsync(() => overview.IsVisible && overviewDays.Items.Count == 1);
            await FlushAsync(overview);
            CheckFilterCounts("2", "1", "1");
            static bool HasStatusMark(Panel panel, bool completed)
            {
                var mark = panel.Children.OfType<Control>().SingleOrDefault();
                if (mark is null) return false;
                mark.ApplyTemplate();
                return mark.Template.FindName("Dot", mark) is System.Windows.Shapes.Ellipse { Fill: not null, Stroke: null }
                    && (mark.Template.FindName("Check", mark) is System.Windows.Shapes.Path) == completed;
            }
            foreach (var (name, label, count) in new[] { ("NgAllFilter", "全部", 2), ("NgPendingFilter", "未标注", 1), ("NgDoneFilter", "已标注", 1) })
            {
                var statusFilter = (RadioButton)browser.FindName(name);
                if (!Equals(statusFilter.ToolTip, label) || statusFilter.Content is not StackPanel filterContent
                    || !filterContent.Children.OfType<TextBlock>().Any(text => text.Text == $"{label} {count}")
                    || (name == "NgAllFilter"
                        ? !filterContent.Children.OfType<System.Windows.Shapes.Path>().Any()
                        : !HasStatusMark(filterContent, name == "NgDoneFilter")))
                    throw new Exception($"「{label}」筛选应同时显示图标、状态文字和数量");
            }
            pendingFilter.IsChecked = true;
            await FlushAsync(overview);
            CheckFilterCounts("2", "1", "1");
            var pending = FindTextBlock(overview, "1004") ?? throw new Exception("机台总览应显示未标注的 NG 批次");
            if (!pending.IsVisible || FindTextBlock(overview, "1001") is not null)
                throw new Exception("仅看未标注应隐藏已标注批次并展开首个日期");
            var batchButton = FindButton(overview, "标注批次 1004") ?? throw new Exception("总览应支持点击整个批次卡片");
            if (batchButton.Content is not DockPanel batchContent
                || !HasStatusMark(batchContent, completed: false)
                || System.Windows.Automation.AutomationProperties.GetHelpText(batchButton) != "未标注"
                || FindTextBlock(batchButton, "1004") is null || FindTextBlock(batchButton, "未标注") is not null
                || FindButton(overview, "标注") is not null)
                throw new Exception("批次号和状态应在同一个可点击卡片内，且只有一个标注入口");
            if (FindTextBlock(overview, "批次") is { IsVisible: true } || FindTextBlock(overview, "操作") is { IsVisible: true })
                throw new Exception("日期展开后不应显示重复表头");
            batchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
            if (viewer.CurrentPath != dateFolder + "/1004/image.png")
                throw new Exception("从总览应打开对应的 NG 批次");
            await WaitAsync(() => FindImage(viewer)?.Source is not null);
            SelectAnnotationOption(viewer, "categoryPicker", "测试归类");
            SelectAnnotationOption(viewer, "planPicker", "测试方案");
            FindButtonByContent(viewer, "提交标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible && overviewDays.Items.Count == 0 && overview.IsEnabled);
            if (pendingFilter.IsChecked != true) throw new Exception("提交标注后应保留未标注筛选");
            CheckFilterCounts("2", "0", "2");
            ((RadioButton)browser.FindName("NgAllFilter")).IsChecked = true;
            await FlushAsync(overview);
            if (!FindTextBlocks(overview).Any(text => text.IsVisible && text.Text == "2/2")
                || FindTextBlock(overview, "1004") is not { IsVisible: true })
                throw new Exception("总览应保留日期的展开状态并更新已标注计数");
            if (FindButton(overview, "标注批次 1004")?.Content is not DockPanel completedBatch
                || !HasStatusMark(completedBatch, completed: true))
                throw new Exception("提交后的卡片应显示实心圆内的对勾");
            var showProgress = (Button?)browser.FindName("NgOverviewOpenButton") ?? throw new Exception("缺少 NG 标注进度入口");
            if (showProgress.IsVisible || FindTextBlocks(overview).Any(text => text.IsVisible && text.Text.Contains("NG 标注进度")))
                throw new Exception("进度页应隐藏重复标题和工具栏入口");
            var returnButton = (Button)browser.FindName("NgOverviewReturnButton");
            if (returnButton.Content is not System.Windows.Shapes.Path
                || System.Windows.Automation.AutomationProperties.GetName(returnButton) != "返回文件列表")
                throw new Exception("返回文件列表应是有辅助名称的图标按钮");
            returnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (overview.IsVisible || !grid.IsVisible) throw new Exception("机台总览应能切回文件列表");
            if (!showProgress.IsVisible || showProgress.Content is not StackPanel progressContent
                || !progressContent.Children.OfType<TextBlock>().Any(label => label.Text == "进度")
                || progressContent.Children.OfType<System.Windows.Shapes.Path>().SingleOrDefault()?.Data is null
                || !Equals(showProgress.Style, annotate.Style)
                || System.Windows.Automation.AutomationProperties.GetName(showProgress) != "NG 标注进度")
                throw new Exception("返回文件列表后，进度入口应和标注按钮同样显示图标与短名称");
            showProgress.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !browser.IsBusy && overview.IsVisible);
            var startInput = (TextBox)browser.FindName("NgStartDate");
            var overviewMessage = (TextBlock)browser.FindName("NgOverviewMessage");
            startInput.Text = "2026-11-31";
            var query = FindButtonByContent(overview, "查询") ?? throw new Exception("总览缺少查询入口");
            query.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pendingFilter.IsChecked = true;
            if (overviewDays.Items.Count != 0 || !overviewMessage.Text.Contains("有效日期"))
                throw new Exception("无效日期查询后切换筛选应保留错误提示并清除旧结果");
            CheckFilterCounts("", "", "");
            var today = DateTime.Today;
            Directory.CreateDirectory(Path.Combine(root, "A601", today.ToString("yyyyMMdd")));
            async Task CheckDatePreset(string label, DateTime expectedStart)
            {
                var observed = new TaskCompletionSource<(DateTime Start, DateTime End)>(TaskCreationOptions.RunContinuationsAsynchronously);
                service.NgBatchQuery = (_, start, end, _) =>
                {
                    observed.TrySetResult((start, end));
                    return Task.FromResult(Array.Empty<RemoteBatchKey>());
                };
                var preset = FindButtonByContent(overview, label) ?? throw new Exception("总览缺少日期快捷筛选：" + label);
                preset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var range = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitAsync(() => !browser.IsBusy);
                if (range.Start != expectedStart || range.End != today.AddDays(1)
                    || startInput.Text != expectedStart.ToString("yyyy-MM-dd")
                    || ((TextBox)browser.FindName("NgEndDate")).Text != today.ToString("yyyy-MM-dd")
                    || pendingFilter.IsChecked != true)
                    throw new Exception("日期快捷筛选应立即查询正确范围，并保留标注状态筛选：" + label);
            }
            await CheckDatePreset("今天", today);
            await CheckDatePreset("近 7 天", today.AddDays(-6));
            Console.WriteLine("通过：机台 NG 总览、按状态筛选、直接提交标注并刷新日期计数");
            Console.WriteLine("通过：通过两个加号新增字典，自动选中新值并刷新下拉选项");
            Console.WriteLine("通过：已标注图片可一次撤销全部记录，关窗后刷新列表");
            Console.WriteLine("通过：日期目录单选、标注窗口只开一个、显示目录标题、提交后关闭并刷新列表");
        }
        finally { host.Close(); }
    }
    private static void CheckRoundedButtonFocus(Button button)
    {
        button.ApplyTemplate();
        if (button.FocusVisualStyle is not { } focusStyle
            || button.Template.FindName("Chrome", button) is not Border { CornerRadius.TopLeft: > 0 }
            || button.Template.Triggers.OfType<Trigger>().Any(trigger =>
                trigger.Property == UIElement.IsKeyboardFocusedProperty))
            throw new Exception("按钮应只在键盘导航时绘制额外焦点提示");
        var focusCue = new Control { Style = focusStyle };
        if (focusCue.Template?.LoadContent() is not Border { CornerRadius.TopLeft: > 0, BorderThickness.Left: 1 })
            throw new Exception("键盘焦点提示应为细圆角框");
    }

    private static void CheckAnnotationInputs(ImageViewer viewer)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var note = (TextBox)typeof(ImageViewer).GetField("noteBox", flags)!.GetValue(viewer)!;
        var operatorBox = (TextBox)typeof(ImageViewer).GetField("operatorBox", flags)!.GetValue(viewer)!;
        var singleLineFields = new Control[]
        {
            (Button)typeof(ImageViewer).GetField("categoryPicker", flags)!.GetValue(viewer)!,
            (Button)typeof(ImageViewer).GetField("planPicker", flags)!.GetValue(viewer)!,
            FindButton(viewer, "新增异常归类")!,
            FindButton(viewer, "新增异常处理方案")!,
            operatorBox
        };
        note.Text = "第一行备注\n第二行备注";
        viewer.UpdateLayout();
        if (singleLineFields.Any(field => Math.Abs(field.ActualHeight - operatorBox.ActualHeight) > 0.5
            || field.FocusVisualStyle is not null || field.BorderThickness != operatorBox.BorderThickness))
            throw new Exception("标注单行控件应等高，并采用无额外外框的焦点样式");
        foreach (var input in new[] { note, operatorBox })
        {
            var first = input.GetRectFromCharacterIndex(0);
            if (first.IsEmpty || first.X > input.BorderThickness.Left + input.Padding.Left + 4)
                throw new Exception($"输入起点应贴合边框内侧，实际 X={first.X}");
        }
        var noteStart = note.GetRectFromCharacterIndex(0);
        if (noteStart.Top > note.BorderThickness.Top + note.Padding.Top + 2 || note.GetRectFromCharacterIndex(6).Top <= noteStart.Top)
            throw new Exception("备注应从左上开始并支持换行输入");
        note.Clear();
    }

    private static async Task AddAnnotationOptionAsync(ImageViewer viewer, string addLabel, string name)
    {
        var add = FindButton(viewer, addLabel) ?? throw new Exception("找不到字典新增按钮：" + addLabel);
        viewer.UpdateLayout();
        if (add.Content is not System.Windows.Shapes.Path { Data: { } geometry } glyph)
            throw new Exception("新增按钮应使用居中矢量加号");
        var bounds = geometry.Bounds;
        var center = glyph.TranslatePoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), add);
        if (Math.Abs(center.X - add.ActualWidth / 2) > 1 || Math.Abs(center.Y - add.ActualHeight / 2) > 1)
            throw new Exception("新增按钮的加号应在方块正中央");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = AnnotationPopup(viewer);
        var editor = (StackPanel)((Border)popup.Child).Child;
        var input = editor.Children.OfType<TextBox>().Single();
        var save = editor.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, "保存"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!popup.IsOpen || !editor.Children.OfType<TextBlock>().Any(text => text.Text == "请输入名称" && text.Visibility == Visibility.Visible))
            throw new Exception("空白名称应在字典编辑框中提示错误");
        input.Text = name;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !popup.IsOpen && FindTextBlocks(viewer).Any(block => block.Text == name));
    }
    private static System.Windows.Controls.Primitives.Popup AnnotationPopup(ImageViewer viewer) =>
        (System.Windows.Controls.Primitives.Popup)typeof(ImageViewer).GetField("pickerPopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewer)!;
    private static void SelectAnnotationOption(ImageViewer viewer, string pickerName, string? name = null)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var picker = (Button)typeof(ImageViewer).GetField(pickerName, flags)!.GetValue(viewer)!;
        picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = AnnotationPopup(viewer);
        var options = (StackPanel)((ScrollViewer)((Border)popup.Child).Child).Content;
        options.Children.OfType<Button>().Single(button => name is null || button.Content is TextBlock text && text.Text == name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task ArchiveUiAsync(ClientBrowser browser, ServerWindow server, string root, string temp)
    {
        var tree = (TreeView)browser.FindName("FolderTree");
        var grid = (DataGrid)browser.FindName("FileGrid");
        var rootNode = (TreeViewItem)tree.Items[0];
        var folder = rootNode.Items.Cast<TreeViewItem>().Single(n => ((RemoteEntry)n.Tag).Name == "文本");
        var folderMenu = folder.ContextMenu?.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray() ?? [];
        if (!folderMenu.SequenceEqual(new[] { "复制" })) throw new Exception("普通目录树节点右键应只显示复制");
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
        // 非日期层/非对线文件夹不允许标注
        grid.SelectedItem = directory; grid.UpdateLayout();
        if (((Button)browser.FindName("AnnotateButton")).IsEnabled) throw new Exception("非对线文件夹不应允许标注");
        grid.SelectedItem = zip; grid.UpdateLayout();
        var row = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(directory)
            ?? throw new Exception("未找到用于右键点击的目录行");
        grid.SelectedItems.Add(grid.Items.Cast<RemoteEntry>().First(item => item != zip && item != directory));
        var selectedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(zip);
        selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
        if (grid.SelectedItems.Count != 2 || !grid.SelectedItems.Contains(zip))
            throw new Exception("多选列表右键已选中的行应保留当前选择");
        string before = $"before={(grid.SelectedItem as RemoteEntry)?.Name ?? "(null)"}/rowSelected={row.IsSelected}";
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
        // 实际打开行菜单，确认菜单项对应被右键的文件夹而非原选中的 ZIP。
        if (!ReferenceEquals(grid.SelectedItem, directory) || grid.SelectedItems.Count != 1)
            throw new Exception($"列表右键未选中对应行；{before} after={(grid.SelectedItem as RemoteEntry)?.Name ?? "(null)"} rowSelectedNow={row.IsSelected} count={grid.SelectedItems.Count}");
        // 触发框架的 ContextMenuOpening 路由事件，使用用户点击时的处理器。
        OpenContext(row);
        var directoryMenu = row.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
        if (!directoryMenu.SequenceEqual(new[] { "复制" })) throw new Exception("普通文件夹右键应只显示复制");
        var copyButton = row.ContextMenu.Items.OfType<MenuItem>().First();
        if (copyButton.Icon is not System.Windows.Shapes.Path || copyButton.InputGestureText != "Ctrl+C")
            throw new Exception("文件列表的复制菜单应显示图标和快捷键");
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
    private static void CheckLegacyClientSettingsMigration(string temp)
    {
        string source = Path.Combine(temp, "legacy-client-settings");
        string target = Path.Combine(temp, "migrated-client-settings");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var migrate = typeof(RemoteClient::RemoteFiles.Client.App).GetMethod("MigrateLegacySettings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        void RunMigration() => migrate.Invoke(null, [source, target]);

        foreach (string name in new[] { "remote-client.json", "theme.json", "layout-remote-client.json", "operator.json", "ngfilter.json" })
            File.WriteAllText(Path.Combine(source, name), name);
        string localTheme = Path.Combine(target, "theme.json");
        File.WriteAllText(localTheme, "部署目录已有主题");
        string legacyOperator = Path.Combine(source, "operator.json");
        string localOperator = Path.Combine(target, "operator.json");
        using (File.Open(legacyOperator, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            RunMigration();
            if (File.Exists(localOperator)) throw new Exception("旧操作人配置被锁定时应留待下次启动重试");
        }
        RunMigration();
        foreach (string name in new[] { "remote-client.json", "layout-remote-client.json", "operator.json" })
            if (File.ReadAllText(Path.Combine(target, name)) != name) throw new Exception("客户端旧配置未迁移：" + name);
        if (File.ReadAllText(localTheme) != "部署目录已有主题" || File.Exists(Path.Combine(target, "ngfilter.json")))
            throw new Exception("迁移应保留已有客户端配置并限制迁移范围");
        File.Delete(localOperator);
        RunMigration();
        if (File.Exists(localOperator) || File.ReadAllText(legacyOperator) != "operator.json"
            || Directory.EnumerateFiles(target, "*.tmp").Any())
            throw new Exception("迁移应只成功执行一次，保留旧配置并清理临时文件");
        Console.WriteLine("通过：旧客户端配置迁移、已有设置保留、失败重试和重复启动");
    }

    private static async Task CheckViewerMinimizeAsync(ImageViewer viewer)
    {
        var owner = viewer.Owner ?? throw new Exception("查看器应从文件列表打开");
        var path = viewer.CurrentPath;
        var minimize = FindButton(viewer, "最小化") ?? throw new Exception("查看器缺少最小化按钮");
        minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => viewer.WindowState == WindowState.Minimized);

        var handle = new System.Windows.Interop.WindowInteropHelper(viewer).Handle;
        if (!GetWindowRect(handle, out var bounds)) throw new Exception("无法读取最小化窗口的位置");
        if (!viewer.ShowInTaskbar || MonitorFromRect(ref bounds, 0) != IntPtr.Zero)
            throw new Exception($"查看器最小化后应从任务栏恢复，桌面不能留有横条（窗口坐标 {bounds.Left}, {bounds.Top}）");

        System.Windows.SystemCommands.RestoreWindow(viewer);
        await WaitAsync(() => viewer.WindowState == WindowState.Normal);
        if (!viewer.IsVisible || viewer.Owner != owner || viewer.CurrentPath != path)
            throw new Exception("从最小化恢复后应保留所属窗口和当前图片");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowBounds { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref WindowBounds bounds, uint flags);

    private static void CheckInlineAnnotationPath(ImageViewer window, string folderPath)
    {
        window.UpdateLayout();
        var path = FindTextBlocks(window).Single(block => block.Text == folderPath);
        var title = FindTextBlocks(window).Single(block => Equals(block.ToolTip, window.CurrentPath));
        var pathCenter = path.TranslatePoint(new Point(0, path.ActualHeight / 2), window);
        var titleCenter = title.TranslatePoint(new Point(title.ActualWidth / 2, title.ActualHeight / 2), window);
        if (Math.Abs(pathCenter.Y - titleCenter.Y) > 1 || Math.Abs(titleCenter.X - window.ActualWidth / 2) > 1
            || pathCenter.X + path.ActualWidth >= titleCenter.X - title.ActualWidth / 2)
            throw new Exception("短路径应填入标题栏左侧，图片名保持居中且不重叠");
    }

    private static void CheckAnnotationTitle(ImageViewer window, string name, string folderPath)
    {
        window.UpdateLayout();
        var title = FindTextBlocks(window).SingleOrDefault(block => block.Text == name && Equals(block.ToolTip, window.CurrentPath))
            ?? throw new Exception("标注窗口标题栏应显示当前图片名");
        var path = FindTextBlocks(window).Single(block => block.Text == folderPath);
        double titleCenter = title.TranslatePoint(new Point(title.ActualWidth / 2, 0), window).X;
        if (Math.Abs(titleCenter - window.ActualWidth / 2) > 1) throw new Exception("图片名应保持居中");
        var pathTop = path.TranslatePoint(new Point(0, 0), window).Y;
        var titleBottom = title.TranslatePoint(new Point(0, title.ActualHeight), window).Y;
        var listTop = FindTextBlocks(window).Single(block => block.Text == "图片列表").TranslatePoint(new Point(0, 0), window).Y;
        if (path.TextTrimming != TextTrimming.None || path.TextWrapping != TextWrapping.Wrap || path.ActualHeight <= title.ActualHeight
            || pathTop < titleBottom || pathTop + path.ActualHeight > listTop)
            throw new Exception("长路径应在图片名下方完整换行显示，并且不遮挡图片列表");
    }
    private static void CheckImageName(ImageViewer window, string name)
    {
        var blocks = FindTextBlocks(window).ToArray();
        // 名称只应出现在标题栏一处，底部状态栏显示格式、尺寸和大小。
        int occurrences = blocks.Count(b => b.Text.Contains(name));
        if (occurrences != 1) throw new Exception($"图片名称应只出现一次，实际 {occurrences} 次：{name}");
        var title = blocks.FirstOrDefault(b => b.Text == name && b.FontWeight.ToOpenTypeWeight() >= 600)
            ?? throw new Exception("标题栏未加粗显示图片名称：" + name);
        if (title.ToolTip?.ToString()?.EndsWith(name) != true) throw new Exception("标题未显示完整路径");
        if (!blocks.Any(b => b.Text.Contains("PNG")) || !blocks.Any(b => b.Text.Contains("×")))
            throw new Exception("底部未显示图片格式和尺寸");
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
        // 图片切换箭头改为悬停显示后不再挂 ToolTip，名字改由 AutomationProperties 提供。
        if (parent is Button button && (Equals(button.ToolTip, tooltip) || System.Windows.Automation.AutomationProperties.GetName(button) == tooltip)) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindButton(VisualTreeHelper.GetChild(parent, i), tooltip) is { } child) return child;
        return null;
    }
    private static TextBlock? FindTextBlock(DependencyObject parent, string text)
    {
        if (parent is TextBlock block && block.Text == text) return block;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindTextBlock(VisualTreeHelper.GetChild(parent, i), text) is { } child) return child;
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
    private static RemoteEntry? SelectedTreeEntry(TreeView tree)
    {
        static RemoteEntry? Walk(ItemCollection items)
        {
            foreach (var item in items.OfType<TreeViewItem>())
            {
                if (item.IsSelected) return item.Tag as RemoteEntry;
                if (Walk(item.Items) is { } nested) return nested;
            }
            return null;
        }
        return Walk(tree.Items);
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
