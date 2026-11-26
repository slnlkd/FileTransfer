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
    public static async Task CheckImageViewerGroupAsync(string temp)
    {
        string root = Path.Combine(temp, "image-viewer-ui");
        Directory.CreateDirectory(Path.Combine(root, "文本"));
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.dat"), "其他类型");
        foreach (string name in new[] { "late.png", "early.png" })
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, name)); encoder.Save(output);
        }
        File.SetCreationTimeUtc(Path.Combine(root, "early.png"), DateTime.UtcNow.AddDays(-1));
        await using var service = new RemoteFileService(root);
        service.Start(0);
        var client = new ClientWindow(Path.Combine(temp, "image-client.json"));
        Prepare(client); client.Show();
        try
        {
            var browser = (ClientBrowser)client.FindName("Browser");
            await browser.ConnectAsync(new("127.0.0.1", service.Port));
            await CheckImageViewerAsync(client, browser, (DataGrid)browser.FindName("FileGrid"), root, service.Port);
        }
        finally { await CloseAsync(client); }
    }

    private static async Task CheckImageViewerAsync(ClientWindow client, ClientBrowser browser, DataGrid grid, string root, int port)
    {
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
        await CheckImageContextMenuAsync(imageWindow, Path.Combine(root, "early.png"));
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
        await WaitAsync(() => FindImage(annotationWindow)?.Source != null);
        await CheckImageContextMenuAsync(annotationWindow, Path.Combine(root, annotationEntries[0].Name));
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
        var historyPanel = (StackPanel)AnnotationField(annotationWindow, "annotationHistory");
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
        var note = (TextBox)AnnotationField(annotationWindow, "noteBox");
        var preview = (Grid)typeof(ImageViewer).GetField("stage", viewerFields)!.GetValue(annotationWindow)!;
        var source = PresentationSource.FromVisual(annotationWindow);
        var rightInNote = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        note.RaiseEvent(rightInNote);
        if (!rightInNote.Handled)
            throw new Exception("标注窗口应在备注框获得焦点时接收方向键切图");
        await WaitAsync(() => annotationWindow.CurrentPath == annotationEntries[1].Path);
        preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
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
        var form = (Border)((AnnotationPanel)columns.Children.Cast<UIElement>().Single(child => Grid.GetColumn(child) > Grid.GetColumn(splitter))).Content;
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
        leftList.SelectedIndex = 1;
        await WaitAsync(() => annotationWindow.CurrentPath == annotationEntries[1].Path);
        var filter = ((DockPanel)leftList.Parent).Children.OfType<CheckBox>().Single();
        if (!Equals(filter.Content, "仅看已标注")) throw new Exception("图片列表筛选应为仅看已标注");
        filter.IsChecked = true;
        filter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await WaitAsync(() => leftList.Items.Count == 1 && annotationWindow.CurrentPath == annotationEntries[0].Path);
        await annotationWindow.MoveAsync(-1);
        if (annotationWindow.CurrentPath != annotationEntries[0].Path) throw new Exception("已标注筛选时切图应跳过未标注图片");
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
        Console.WriteLine("通过：标注窗口布局、全窗口方向键切图、标注进度、历史和已标注筛选");
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
    }
}
