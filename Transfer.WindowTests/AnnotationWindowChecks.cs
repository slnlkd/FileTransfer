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
using AnnotationPanel = RemoteClient::RemoteFiles.Desktop.AnnotationPanel;
using ImageThumbItem = RemoteClient::RemoteFiles.Desktop.ImageThumbItem;
using ProcessViewer = RemoteClient::RemoteFiles.Desktop.ProcessViewer;

internal static partial class RemoteWindowChecks
{
    private static async Task CheckNextNgBatchAsync(string temp)
    {
        string root = Path.Combine(temp, "next-ng-batch");
        (string Folder, bool Annotated)[] batches =
        [
            ("A601/20261120/1", false), ("A601/20261120/2", true),
            ("A601/20261120/3", false), ("A601/20261119/4", false),
            ("A601/20261119/5", true)
        ];
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        foreach (var batch in batches)
        {
            Directory.CreateDirectory(Path.Combine(root, batch.Folder));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, batch.Folder, "image.png"));
            encoder.Save(output);
        }
        string repositoryPath = Path.Combine(temp, "next-ng-batch.json");
        await File.WriteAllTextAsync(repositoryPath, JsonSerializer.Serialize(new
        {
            dictionary = new RemoteDictionary([new("category", "测试归类")], [new("plan", "测试方案")]),
            annotations = Array.Empty<RemoteAnnotation>()
        }));
        var repository = new JsonAnnotationRepository(repositoryPath);
        var releaseOverview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDictionary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedRepository = new GatedAnnotationRepository(repository) { DictionaryRelease = releaseDictionary };
        gatedRepository.Release.SetResult();
        bool delayOverview = false;
        int overviewRequests = 0;
        RemoteBatchKey BatchKey(string folder) => new(DateTime.ParseExact(folder.Split('/')[1], "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture), folder.Split('/')[2]);
        foreach (var batch in batches.Where(batch => batch.Annotated))
            await repository.SaveAsync(new RemoteAnnotation(batch.Folder, "A601", AutoLineDate: BatchKey(batch.Folder).Date,
                BatchNumber: BatchKey(batch.Folder).Batch, ImageName: "image.png"));
        await using var service = new RemoteFileService(root, gatedRepository)
        {
            ListFilter = (_, _, entries) => Task.FromResult(new RemoteFilterResult(entries)),
            NgBatchQuery = async (_, _, _, ct) =>
            {
                Interlocked.Increment(ref overviewRequests);
                if (delayOverview) await releaseOverview.Task.WaitAsync(ct);
                return batches.Select(batch => BatchKey(batch.Folder)).ToArray();
            }
        };
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        var browser = new ClientBrowser();
        var host = new Window { Content = browser, Width = 900, Height = 220 };
        var otherApp = new Window { Title = "其他应用模拟窗口", Width = 400, Height = 250 };
        Prepare(host);
        Prepare(otherApp);
        host.Show();
        host.Activate();
        try
        {
            await browser.ConnectAsync(endpoint);
            ((TextBox)browser.FindName("NgStartDate")).Text = "2026-11-19";
            ((TextBox)browser.FindName("NgEndDate")).Text = "2026-11-20";
            typeof(ClientBrowser).GetMethod("SetLocalFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(browser, ["ng"]);
            await WaitAsync(() => !browser.IsBusy);
            var machine = (await endpoint.CallAsync(new("list"))).Entries!.Single();
            await browser.OpenAsync(machine);
            var overview = (FrameworkElement)browser.FindName("NgOverviewPanel");
            var overviewDays = (ItemsControl)browser.FindName("NgOverviewDays");
            await FlushAsync(browser);
            int initialRequests = Volatile.Read(ref overviewRequests);
            delayOverview = true;
            var first = FindButton(overview, "标注批次 1") ?? throw new Exception("缺少首个待标注卡片");
            var originalRows = overviewDays.ItemsSource;
            var originalDayContainer = overviewDays.ItemContainerGenerator.ContainerFromIndex(0);
            first.Focus();
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
            var viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
            await WaitAsync(() => FindImage(viewer)?.Source is not null);
            var firstPicker = (Button)AnnotationField(viewer, "categoryPicker");
            if (firstPicker.IsEnabled) throw new Exception("字典请求未结束时，首图可见但归类控件应保持禁用");
            releaseDictionary.SetResult();
            await WaitAsync(() => firstPicker.IsEnabled);
            otherApp.Show();
            otherApp.Activate();
            await WaitAsync(() => otherApp.IsActive);
            viewer.Close();
            await WaitAsync(() => overview.IsEnabled && host.IsActive);
            if (!ReferenceEquals(originalRows, overviewDays.ItemsSource)
                || !ReferenceEquals(originalDayContainer, overviewDays.ItemContainerGenerator.ContainerFromIndex(0))
                || !ReferenceEquals(first, FindButton(overview, "标注批次 1")))
                throw new Exception("普通关窗刷新应复用日期和卡片容器");
            if (viewer.AnnotationSubmitted || ((TextBlock)browser.FindName("StatusText")).Text.StartsWith("下一未标注"))
                throw new Exception("普通关闭标注窗口时不应自动定位下一批");
            if (Volatile.Read(ref overviewRequests) != initialRequests)
                throw new Exception("普通关闭标注窗口不应重查 NG 总览");
            ((RadioButton)browser.FindName("NgPendingFilter")).IsChecked = true;
            await FlushAsync(browser);
            first = FindButton(overview, "标注批次 1") ?? throw new Exception("未标注筛选丢失首个批次");
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            int successfulWrites = 0;
            foreach (var (current, next) in new[] { ("1", "3"), ("3", "4"), ("4", "") })
            {
                await WaitAsync(() => !browser.IsBusy && host.OwnedWindows.OfType<ImageViewer>().Any());
                viewer = host.OwnedWindows.OfType<ImageViewer>().Single();
                if (!viewer.CurrentPath.Contains("/" + current + "/")) throw new Exception("Enter 打开的批次不正确");
                await WaitAsync(() => FindImage(viewer)?.Source is not null);
                if (Volatile.Read(ref gatedRepository.DictionaryReads) != 1 + successfulWrites)
                    throw new Exception("同连接连续标注应复用字典，只保留旧提交路径必要的服务端校验");
                SelectAnnotationOption(viewer, "categoryPicker", "测试归类");
                SelectAnnotationOption(viewer, "planPicker", "测试方案");
                var rowsBeforeSubmit = overviewDays.ItemsSource;
                var firstDayBeforeSubmit = overviewDays.ItemContainerGenerator.ContainerFromIndex(0);
                otherApp.Activate();
                await WaitAsync(() => otherApp.IsActive);
                FindButtonByContent(viewer, "提交标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => !viewer.IsVisible && overview.IsEnabled && host.IsActive);
                if (!ReferenceEquals(rowsBeforeSubmit, overviewDays.ItemsSource)
                    || current == "1" && !ReferenceEquals(firstDayBeforeSubmit, overviewDays.ItemContainerGenerator.ContainerFromIndex(0)))
                    throw new Exception("提交后的进度更新应复用现有列表与未移除的日期容器");
                if (!viewer.AnnotationSubmitted) throw new Exception("成功提交后应记录提交结果");
                successfulWrites++;
                if (next.Length == 0)
                {
                    await WaitAsync(() => ((TextBlock)browser.FindName("StatusText")).Text.Contains("均已标注"));
                    break;
                }
                await WaitAsync(() => FindButton(overview, "标注批次 " + next)?.IsKeyboardFocused == true);
                await WaitAsync(() => Volatile.Read(ref overviewRequests) > initialRequests);
                var nextButton = FindButton(overview, "标注批次 " + next)!;
                // BringIntoView 在布局队列中完成；获焦并不代表滚动已经结束。
                await FlushAsync(nextButton);
                var scroll = (ScrollViewer)browser.FindName("NgOverviewScroll");
                var bounds = nextButton.TransformToAncestor(scroll).TransformBounds(new Rect(nextButton.RenderSize));
                if (!nextButton.IsVisible || bounds.Top < -1 || bounds.Bottom > scroll.ActualHeight + 1)
                    throw new Exception("下一批卡片应自动展开并滚动到可见区域");
                nextButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent });
            }
            Console.WriteLine("通过：提交后跳过已标注批次、跨日期展开并定位，Enter 打开下一批，普通关闭保持位置");
            Console.WriteLine("通过：切换其他窗口后关窗回到主页，进度刷新保留列表和日期卡片容器");
        }
        finally
        {
            releaseOverview.TrySetResult();
            releaseDictionary.TrySetResult();
            await CloseAsync(otherApp);
            await CloseAsync(host);
        }
    }

    private static async Task CheckAnnotationReopenAsync(string temp)
    {
        if (typeof(ClientBrowser).Assembly.GetType("RemoteFiles.Desktop.ViewerCoordinator") is null)
            throw new Exception("查看窗口生命周期应由独立协调器管理");
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
            dictionary = new RemoteDictionary([new("category", "测试归类")], [new("plan", "测试方案")])
            {
                Remarks = [new("预置备注", "预置备注")]
            },
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
            if (FindDescendant<AnnotationPanel>(viewer) is null)
                throw new Exception("标注表单应由独立 AnnotationPanel 承担");
            await WaitAsync(() => FindButton(viewer, "添加到备注：预置备注") is not null);
            var initialNote = (TextBox)AnnotationField(viewer, "noteBox");
            FindButton(viewer, "添加到备注：预置备注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (initialNote.Text != "预置备注") throw new Exception("默认进入应加载字典为可点击备注标签");
            initialNote.Clear();
            await CheckViewerMinimizeAsync(viewer);
            if (!FindTextBlocks(viewer).Any(block => block.Text == $"annotation-ui/{batch}")) throw new Exception("顶栏未显示完整共享路径");
            CheckInlineAnnotationPath(viewer, $"annotation-ui/{batch}");
            if (FindButtonByContent(viewer, "撤销标注") is { IsVisible: true }) throw new Exception("未标注图片应隐藏撤销按钮");
            var initiallyUnmarkedList = (ListBox)typeof(ImageViewer).GetField("annotationList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewer)!;
            var markedFilter = ((DockPanel)initiallyUnmarkedList.Parent).Children.OfType<CheckBox>().Single();
            string beforeFilter = viewer.CurrentPath;
            markedFilter.IsChecked = true;
            markedFilter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            if (initiallyUnmarkedList.Items.Count != 0 || viewer.CurrentPath != beforeFilter
                || !FindTextBlocks(viewer).Any(block => block.IsVisible && block.Text == "本批次暂无已标注图片"))
                throw new Exception("尚无已标注图片时应显示空状态并保留当前预览");
            markedFilter.IsChecked = false;
            markedFilter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            string imageName = Path.GetFileName(viewer.CurrentPath);
            var submit = FindButtonByContent(viewer, "提交标注") ?? throw new Exception("找不到提交标注按钮");
            CheckRoundedButtonFocus(submit);
            const System.Reflection.BindingFlags fieldFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void CheckMissingField(string name)
            {
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!viewer.IsVisible || annotate.IsEnabled) throw new Exception("表单验证失败后应保持标注窗口打开");
                if (FocusManager.GetFocusedElement(viewer) != AnnotationField(viewer, name))
                    throw new Exception("表单验证失败后应聚焦第一个缺失字段：" + name);
            }
            CheckMissingField("categoryPicker");
            SelectAnnotationOption(viewer, "categoryPicker");
            CheckMissingField("planPicker");
            SelectAnnotationOption(viewer, "planPicker");
            var operatorInput = (TextBox)AnnotationField(viewer, "operatorBox");
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
            if (allMarkedList.Items.Count != 2 || viewer.CurrentPath != unannotatedImage.Path)
                throw new Exception("仅看已标注应保留所有已标注图片和当前预览");
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
            await AddAnnotationOptionAsync(viewer, "新增备注标签", "已通知设备");
            await AddAnnotationOptionAsync(viewer, "新增备注标签", "等待复核");
            var remarkNote = (TextBox)AnnotationField(viewer, "noteBox");
            remarkNote.Text = "手写内容";
            var remarkTag = FindButton(viewer, "添加到备注：已通知设备") ?? throw new Exception("新增备注后未刷新标签");
            remarkTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            remarkTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FindButton(viewer, "添加到备注：等待复核")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (remarkNote.Text != "手写内容；已通知设备；等待复核")
                throw new Exception("备注标签应保留手写内容、追加且重复点击不重复填入");
            SelectAnnotationOption(viewer, "categoryPicker", "自定义归类");
            SelectAnnotationOption(viewer, "planPicker", "自定义处理方案");
            FindButtonByContent(viewer, "提交标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible && annotate.IsEnabled);
            if (!(await endpoint.GetAnnotationHistoryAsync(entry.Path)).Any(item => item.ImageName == unannotatedImage.Name
                && item.CategoryName == "自定义归类" && item.PlanName == "自定义处理方案"
                && item.Note == "手写内容；已通知设备；等待复核"))
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
                if (!System.Windows.Controls.Primitives.Selector.GetIsSelected(preset)
                    || System.Windows.Controls.Primitives.Selector.GetIsSelected((Button)browser.FindName(
                        label == "今天" ? "NgLastWeekButton" : "NgTodayButton")))
                    throw new Exception("日期快捷筛选应高亮当前范围，并清除另一项选中态");
                await FlushAsync(overview);
                var queryControls = new Control[]
                {
                    startInput, (TextBox)browser.FindName("NgEndDate"), query, preset,
                    (RadioButton)browser.FindName("NgAllFilter"), pendingFilter,
                    (RadioButton)browser.FindName("NgDoneFilter")
                };
                if (queryControls.Any(control => Math.Abs(control.ActualHeight - 30) > 0.5))
                    throw new Exception("查询输入框、操作按钮和状态筛选应统一为 30 个逻辑像素高");
            }
            await CheckDatePreset("今天", today);
            await CheckDatePreset("近 7 天", today.AddDays(-6));
            // 用相对日期，避免写死某天时正好落在「近 7 天」的起始日上（原写法在 2026-11-25 会误判）。
            startInput.Text = today.AddDays(-3).ToString("yyyy-MM-dd");
            if (System.Windows.Controls.Primitives.Selector.GetIsSelected((Button)browser.FindName("NgTodayButton"))
                || System.Windows.Controls.Primitives.Selector.GetIsSelected((Button)browser.FindName("NgLastWeekButton")))
                throw new Exception("手动编辑为自定义日期范围后应取消快捷范围高亮");
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
        var note = (TextBox)AnnotationField(viewer, "noteBox");
        var operatorBox = (TextBox)AnnotationField(viewer, "operatorBox");
        var singleLineFields = new Control[]
        {
            (Button)AnnotationField(viewer, "categoryPicker"),
            (Button)AnnotationField(viewer, "planPicker"),
            FindButton(viewer, "新增异常归类")!,
            FindButton(viewer, "新增异常处理方案")!,
            FindButton(viewer, "新增备注标签")!,
            operatorBox
        };
        note.Text = "第一行备注\n第二行备注";
        viewer.UpdateLayout();
        var remarkAdd = FindButton(viewer, "新增备注标签")!;
        var planAdd = FindButton(viewer, "新增异常处理方案")!;
        var planInput = (Button)AnnotationField(viewer, "planPicker");
        if (Math.Abs(operatorBox.ActualWidth - planInput.ActualWidth) > 0.5
            || Math.Abs(operatorBox.ActualWidth - note.ActualWidth) > 0.5)
            throw new Exception("操作人、备注与上方输入框的宽度应统一");
        var remarkPosition = remarkAdd.TranslatePoint(new Point(), viewer);
        if (Math.Abs(remarkAdd.ActualWidth - planAdd.ActualWidth) > 0.5
            || Math.Abs(remarkPosition.X - planAdd.TranslatePoint(new Point(), viewer).X) > 0.5
            || Math.Abs(remarkPosition.Y - note.TranslatePoint(new Point(), viewer).Y) > 0.5)
            throw new Exception("备注新增按钮应与上方同尺寸同列，并与备注框顶部对齐");
        if (singleLineFields.Any(field => Math.Abs(field.ActualHeight - operatorBox.ActualHeight) > 0.5
            || field.FocusVisualStyle is not null || field.BorderThickness != operatorBox.BorderThickness))
            throw new Exception("标注单行控件应等高，并采用无额外外框的焦点样式");
        if (Math.Abs(operatorBox.ActualHeight - 32) > 0.5
            || Math.Abs(((Button)AnnotationField(viewer, "submitButton")).ActualHeight - 32) > 0.5)
            throw new Exception("标注表单与提交按钮应统一为 32 个逻辑像素高");
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
        if (popup.Child is not Border { CornerRadius.TopLeft: 6, BorderThickness.Left: 1 })
            throw new Exception("标注字典弹窗应使用统一圆角和细边框");
        if (((Border)popup.Child).Background is not SolidColorBrush popupBackground
            || popupBackground.Color != ((SolidColorBrush)viewer.FindResource("SurfaceBrush")).Color)
            throw new Exception("标注弹窗背景应跟随所在查看器的主题");
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
        (System.Windows.Controls.Primitives.Popup)AnnotationField(viewer, "pickerPopup");
    private static void SelectAnnotationOption(ImageViewer viewer, string pickerName, string? name = null)
    {
        var picker = (Button)AnnotationField(viewer, pickerName);
        picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = AnnotationPopup(viewer);
        var options = (StackPanel)((ScrollViewer)((Border)popup.Child).Child).Content;
        options.Children.OfType<Button>().Single(button => name is null || button.Content is TextBlock text && text.Text == name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    // 通过控件语义定位表单，测试不再要求查看器持有其私有字段。
    private static object AnnotationField(ImageViewer viewer, string name)
    {
        var panel = FindDescendant<AnnotationPanel>(viewer) ?? throw new Exception("缺少独立标注表单");
        if (name == "pickerPopup")
            return typeof(AnnotationPanel).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        if (name == "submitButton") return FindButtonByContent(panel, "提交标注") ?? FindButtonByContent(panel, "新增标注")!;
        string label = name switch
        {
            "noteBox" => "备注", "operatorBox" => "操作人", "categoryPicker" => "异常归类",
            "planPicker" => "异常处理方案", "annotationHistory" => "标注历史",
            _ => throw new ArgumentException(name)
        };
        DependencyObject? Find(DependencyObject parent)
        {
            if (parent is FrameworkElement element && System.Windows.Automation.AutomationProperties.GetName(element) == label) return parent;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Find(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        return Find(panel) ?? throw new Exception("找不到标注控件：" + label);
    }

}
