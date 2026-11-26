using System.IO;
using System.Diagnostics;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>
/// 图片查看器：参考 Windows 照片的深色界面。
/// 默认只显示图片；缩略图条按需展开；右下角提供缩放、实际大小和全屏。
/// </summary>
public sealed class ImageViewer : Window
{
    private readonly RemoteFileClient client;
    private readonly RemoteEntry[] images;
    private readonly Image image = new() { Stretch = Stretch.Fill, RenderTransformOrigin = new Point(0, 0) };
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform translate = new();
    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly Grid stage = new();
    // 指针离开图片区域后延迟 0.9 秒再收起两侧箭头，避免贴着边缘操作时闪烁。
    private readonly DispatcherTimer arrowHideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private const double ArrowDimOpacity = 0.42;
    private readonly TextBlock stageMessage = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
        Margin = new Thickness(24),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly TextBlock formatText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11
    };
    private readonly TextBlock dimensionText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11,
        Margin = new Thickness(0, 0, 0, 0)
    };
    private readonly TextBlock sizeText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11
    };
    private readonly StackPanel infoPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock pixelPosition = new()
    {
        FontSize = 11, Width = 104, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly System.Windows.Documents.Run pixelNumbers = new("—, —");
    private readonly TextBlock titleText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly AnnotationContext? annotation;
    private ListBox? annotationList;
    private AnnotationPanel? annotationPanel;
    private bool annotationDirty => annotationPanel?.Model.IsDirty == true;
    private bool annotationWriting => annotationPanel?.Model.IsWriting == true;
    private bool imageActionBusy;
    private bool onlyAnnotated;
    private readonly Button previousArrow;
    private readonly Button nextArrow;
    private readonly Border titleBar;
    private readonly Border thumbBar;
    private readonly Button thumbToggle;
    private readonly Button fullscreenButton;
    private readonly Button actualSizeButton;
    private readonly System.Windows.Shapes.Path actualSizeIcon;
    private readonly TextBox percentBox = new();
    private readonly Slider zoomSlider = new();
    private readonly System.Windows.Shapes.Path fullscreenIcon;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, BitmapSource> imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> cacheOrder = new();
    private const long CacheLimitBytes = 200L * 1024 * 1024;
    private readonly ObservableCollection<ImageThumbItem> thumbs = [];
    private readonly ListBox thumbList = new();
    private readonly Dictionary<string, BitmapSource> thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> thumbLoading = [];
    private readonly SemaphoreSlim thumbSlots = new(2);
    private readonly System.Windows.Threading.DispatcherTimer thumbScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private ScrollViewer? thumbScrollViewer;
    private double thumbScrollFrom, thumbScrollTo;
    private int thumbScrollElapsed;
    private const int ThumbScrollDuration = 220;
    private bool syncingThumb;
    private bool syncingAnnotation;
    private Task<BitmapSource?>? prefetchTask;
    private string? prefetchPath;
    private CancellationTokenSource? loadCancel;
    private long cacheBytes;
    private int loadVersion;
    private int index;
    private double zoom = 1;
    private bool fitMode = true;
    private bool dragging;
    private bool fullscreen;
    private bool updatingZoom;
    private Point dragStart;
    private double dragOriginX, dragOriginY;
    public string CurrentPath => images[index].Path;
    public bool AnnotationSubmitted { get; private set; }

    public ImageViewer(RemoteFileClient client, RemoteEntry[] images, string selected, AnnotationContext? annotation = null)
    {
        UseDarkTheme();
        this.client = client;
        this.annotation = annotation;
        // 标注模式保持传入顺序（与文件列表一致）；普通查看按创建时间排序。
        this.images = annotation is null
            ? images.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : images.ToArray();
        index = Array.FindIndex(this.images, e => e.Path == selected);
        if (index < 0) throw new IOException("图片已从目录移除，请刷新后重试");
        for (int i = 0; i < this.images.Length; i++)
        {
            var thumb = new ImageThumbItem(i, this.images[i], i + 1);
            if (annotation is not null) thumb.Annotated = annotation.GetImageHistory(this.images[i].Name).Count > 0;
            thumbs.Add(thumb);
        }

        Width = 1180; Height = 820; MinWidth = 720; MinHeight = 480;
        Title = annotation is null ? "图片查看" : annotation.DisplayPath;
        Background = ThemeBrush("CanvasBrush", Color.FromRgb(0x17, 0x1A, 0x1D));
        Foreground = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // 支持独立最小化的窗口需要任务栏入口，否则 Windows 会把它缩成桌面角落的横条。
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 36,
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false
        });

        var line = ThemeBrush("LineBrush", Color.FromRgb(0x36, 0x3C, 0x43));
        var header = ThemeBrush("HeaderBrush", Color.FromRgb(0x20, 0x24, 0x29));
        var surface = ThemeBrush("SurfaceBrush", Color.FromRgb(0x24, 0x28, 0x2D));
        var muted = ThemeBrush("MutedBrush", Color.FromRgb(0xA9, 0xAF, 0xB6));
        var danger = ThemeBrush("DangerBrush", Color.FromRgb(0xF0, 0x8A, 0x80));
        var stageText = new SolidColorBrush(Color.FromRgb(0xAE, 0xB8, 0xC2));

        var infoBrush = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC));
        formatText.Foreground = infoBrush;
        dimensionText.Foreground = infoBrush;
        sizeText.Foreground = infoBrush;
        titleText.Foreground = Foreground;
        stageMessage.Foreground = stageText;
        previousArrow = CreateArrow(false, "上一张", HorizontalAlignment.Left);
        nextArrow = CreateArrow(true, "下一张", HorizontalAlignment.Right);

        var panel = new DockPanel();

        // 等宽的左右区域让文件名始终以整个窗口为中心。
        var titleButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var minimize = CreateTitleButton("IconMinimizeGeometry", "最小化");
        var maximize = CreateTitleButton("IconMaximizeGeometry", "最大化 / 还原");
        var close = CreateTitleButton("IconCloseGeometry", "关闭", "ViewerCloseButton");
        foreach (var button in new[] { minimize, maximize, close })
        {
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            titleButtons.Children.Add(button);
        }
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        maximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        close.Click += (_, _) => Close();
        StateChanged += (_, _) =>
        {
            if (maximize.Content is System.Windows.Shapes.Path icon)
                icon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconRestoreGeometry" : "IconMaximizeGeometry");
        };
        var titleGrid = new Grid { Height = 36 };
        double captionWidth = titleButtons.Children.Count * close.Width;
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = captionWidth });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = captionWidth });
        titleText.Margin = new Thickness(8, 0, 8, 0);
        titleText.TextAlignment = TextAlignment.Center;
        Grid.SetColumn(titleText, 1);
        titleGrid.Children.Add(titleText);
        if (annotation is not null)
        {
            var pathLabel = new TextBlock
            {
                Text = annotation.DisplayPath,
                Foreground = muted,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleGrid.Children.Add(pathLabel);
        }
        titleButtons.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(titleButtons, 2);
        titleGrid.Children.Add(titleButtons);
        titleBar = new Border
        {
            Background = Background,
            Child = titleGrid
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        panel.Children.Add(titleBar);

        // 图片舞台：Canvas + 缩放/平移变换，鼠标滚轮缩放、放大后可拖动。
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translate);
        image.RenderTransform = transform;
        canvas.Children.Add(image);
        stage.Background = Brushes.Transparent;
        stage.FocusVisualStyle = null;
        stage.ClipToBounds = true;
        stage.Children.Add(canvas);
        stage.Children.Add(stageMessage);
        stage.Children.Add(previousArrow);
        stage.Children.Add(nextArrow);
        stage.ContextMenu = BuildImageContextMenu();
        stage.PreviewMouseRightButtonDown += (_, _) => { if (annotation is not null) stage.Focus(); };
        canvas.SizeChanged += (_, _) => { if (fitMode) FitToWindow(); else UpdateCursor(); };
        stage.MouseMove += Stage_MouseMove;
        stage.MouseEnter += Stage_MouseEnter;
        stage.MouseLeave += Stage_MouseLeave;
        arrowHideTimer.Tick += (_, _) =>
        {
            arrowHideTimer.Stop();
            SetArrowVisual(previousArrow, 0, 180);
            SetArrowVisual(nextArrow, 0, 180);
        };
        canvas.PreviewMouseWheel += Canvas_MouseWheel;
        canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        canvas.MouseMove += Canvas_MouseMove;
        canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        canvas.MouseLeave += (_, _) => { if (dragging) EndDrag(); };

        // 底部缩略图条：默认隐藏，点击状态栏左侧的胶片按钮后展开。
        thumbList.ItemsSource = thumbs;
        thumbList.Background = Brushes.Transparent;
        thumbList.BorderThickness = new Thickness(0);
        thumbList.Padding = new Thickness(4, 4, 4, 0);
        thumbList.SelectionChanged += Thumb_SelectionChanged;
        thumbList.PreviewMouseWheel += Thumb_MouseWheel;
        thumbList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Thumb_ScrollChanged));
        thumbList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (thumbList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                Dispatcher.BeginInvoke(new Action(LoadRealizedThumbs), System.Windows.Threading.DispatcherPriority.Background);
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(thumbList, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(thumbList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(thumbList, true);
        VirtualizingPanel.SetVirtualizationMode(thumbList, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(thumbList, ScrollUnit.Pixel);
        var thumbPanel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        thumbPanel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
        thumbList.ItemsPanel = new ItemsPanelTemplate(thumbPanel);
        thumbList.ItemTemplate = BuildThumbTemplate();
        thumbList.ItemContainerStyle = BuildThumbItemStyle();
        if (ThemeStyle("ThumbnailScrollBarStyle") is { } thumbScrollStyle)
            thumbList.Resources.Add(typeof(ScrollBar), thumbScrollStyle);
        thumbList.PreviewMouseDown += (_, _) => thumbScrollTimer.Stop();
        thumbScrollTimer.Tick += (_, _) => TickThumbScroll();
        thumbBar = new Border
        {
            Height = 108,
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0),
            Child = thumbList,
            Visibility = Visibility.Collapsed
        };
        DockPanel.SetDock(thumbBar, Dock.Bottom);

        // 状态栏：左侧缩略图开关 + 图片信息；右侧缩放控件。
        thumbToggle = CreateIconButton("IconFilmstripGeometry", "显示缩略图", out _);
        thumbToggle.Width = 30;
        thumbToggle.Height = 30;
        thumbToggle.Click += (_, _) => ToggleThumbnails();
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(thumbToggle);
        infoPanel.Children.Add(CreateInfoSeparator());
        infoPanel.Children.Add(formatText);
        infoPanel.Children.Add(CreateInfoSeparator());
        infoPanel.Children.Add(CreateInfoIcon("IconDimensionsGeometry"));
        infoPanel.Children.Add(dimensionText);
        infoPanel.Children.Add(CreateInfoSeparator());
        infoPanel.Children.Add(CreateInfoIcon("IconSizeGeometry"));
        infoPanel.Children.Add(sizeText);
        infoPanel.Children.Add(CreateInfoSeparator());
        pixelPosition.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        pixelNumbers.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "AccentBrush");
        pixelPosition.Inlines.Add(pixelNumbers);
        pixelPosition.Inlines.Add(new System.Windows.Documents.Run(" 像素"));
        pixelPosition.ToolTip = "鼠标所在的原图像素坐标";
        infoPanel.Children.Add(pixelPosition);
        left.Children.Add(infoPanel);

        actualSizeButton = CreateIconButton("IconActualSizeGeometry", "缩放到实际大小 (100%)", out var actualSizeGlyph);
        actualSizeIcon = actualSizeGlyph;
        actualSizeButton.Click += (_, _) => ToggleActualSize();

        percentBox.Width = 52; percentBox.Height = 24; percentBox.MinHeight = 22; percentBox.FontSize = 11;
        percentBox.TextAlignment = TextAlignment.Center;
        percentBox.VerticalContentAlignment = VerticalAlignment.Center;
        percentBox.Padding = new Thickness(0);
        percentBox.ToolTip = "输入百分比后按回车，或点右侧箭头选择预设";
        percentBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ApplyPercentText(); } };
        percentBox.LostFocus += (_, _) => UpdateZoomUi();
        percentBox.GotKeyboardFocus += (_, _) => percentBox.SelectAll();
        var percentArrow = CreateIconButton("IconChevronGeometry", "选择缩放比例", out _);
        percentArrow.Width = 22;
        percentArrow.Click += (_, _) => ShowZoomPresets(percentArrow);
        var percentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        percentPanel.Children.Add(percentBox);
        percentPanel.Children.Add(percentArrow);

        var zoomOut = CreateIconButton("IconZoomOutGeometry", "缩小 (Ctrl+-)", out _);
        zoomOut.Click += (_, _) => ZoomAtCenter(zoom / 1.15);
        var zoomIn = CreateIconButton("IconZoomInGeometry", "放大 (Ctrl++)", out _);
        zoomIn.Click += (_, _) => ZoomAtCenter(zoom * 1.15);
        zoomSlider.Width = 110;
        zoomSlider.Height = 20;
        zoomSlider.Minimum = 10;
        zoomSlider.Maximum = 800;
        zoomSlider.IsMoveToPointEnabled = true;
        zoomSlider.VerticalAlignment = VerticalAlignment.Center;
        zoomSlider.ToolTip = "拖动调整缩放";
        zoomSlider.ValueChanged += (_, e) => { if (!updatingZoom) ZoomAtCenter(e.NewValue / 100d, animate: false); };
        fullscreenButton = CreateIconButton("IconExpandGeometry", "全屏 (F11)", out var fullscreenGlyph);
        fullscreenIcon = fullscreenGlyph;
        fullscreenButton.Click += (_, _) => ToggleFullscreen();

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var element in new FrameworkElement[] { actualSizeButton, percentPanel, zoomOut, zoomSlider, zoomIn, fullscreenButton })
        {
            element.Margin = new Thickness(4, 0, 0, 0);
            right.Children.Add(element);
        }

        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.Children.Add(left);
        Grid.SetColumn(right, 1);
        statusGrid.Children.Add(right);
        var statusBar = new Border
        {
            Height = 40,
            Padding = new Thickness(16, 0, 12, 0),
            Background = Background,
            Child = statusGrid
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        panel.Children.Add(statusBar);
        var previewFrame = new Border
        {
            Child = stage,
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("PanelCornerRadius"),
            SnapsToDevicePixels = true
        };
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        var stageClip = new RectangleGeometry
        {
            RadiusX = previewFrame.CornerRadius.TopLeft - previewFrame.BorderThickness.Left,
            RadiusY = previewFrame.CornerRadius.TopLeft - previewFrame.BorderThickness.Top
        };
        stage.Clip = stageClip;
        stage.SizeChanged += (_, e) => stageClip.Rect = new Rect(e.NewSize);
        // 先停靠状态栏，再停靠缩略图条，最后让舞台填充剩余空间。
        panel.Children.Add(thumbBar);
        panel.Children.Add(previewFrame);

        if (annotation is null) Content = panel;
        else
        {
            // 标注模式：标题栏提到最上面，下面分「图片列表 / 大图 / 标注」三栏。
            panel.Children.Remove(titleBar);
            panel.Children.Remove(statusBar);
            var shell = new DockPanel();
            shell.Children.Add(titleBar);
            shell.Children.Add(statusBar);
            var columns = new Grid { Margin = new Thickness(12, 4, 12, 0) };
            var listColumn = new ColumnDefinition { Width = new GridLength(244) };
            columns.ColumnDefinitions.Add(listColumn);
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 260, MaxWidth = 420 });
            var panelLine = ThemeBrush("HairlineBrush", Color.FromRgb(0x2C, 0x31, 0x37));
            columns.Children.Add(BuildAnnotationList(surface, panelLine, muted, listColumn));
            panel.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(panel, 1);
            columns.Children.Add(panel);
            var splitter = new GridSplitter
            {
                Foreground = Brushes.Transparent,
                FocusVisualStyle = null,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ToolTip = "调整标注面板宽度"
            };
            System.Windows.Automation.AutomationProperties.SetName(splitter, "调整标注面板宽度");
            Grid.SetColumn(splitter, 2);
            columns.Children.Add(splitter);
            annotationPanel = new AnnotationPanel(new AnnotationViewModel(new RemoteAnnotationClient(client), annotation,
                AnnotationOperatorSettings.Load(), AnnotationOperatorSettings.Save), Resources);
            annotationPanel.WriteSucceeded += kind =>
            {
                if (lifetime.IsCancellationRequested) return;
                AnnotationSubmitted = kind == AnnotationWriteKind.Submit;
                var owner = Owner;
                Close();
                Toast.Show(owner, kind == AnnotationWriteKind.Submit ? "标注已提交" : "该图片的标注已撤销", ToastKind.Success);
            };
            var annotationSide = annotationPanel;
            Grid.SetColumn(annotationSide, 3);
            columns.Children.Add(annotationSide);
            shell.Children.Add(columns);
            Content = shell;
            // 标注模式下左栏已经承担了切换图片的职责，底部胶片条和它的分隔线一起隐藏。
            thumbToggle.Visibility = Visibility.Collapsed;
            thumbBar.Visibility = Visibility.Collapsed;
            if (infoPanel.Children.Count > 0 && infoPanel.Children[0] is Border) infoPanel.Children.RemoveAt(0);
            // 按屏幕可用区域自适应，不强行放大到 1440×900（高 DPI 下会超出屏幕）。
            var workArea = SystemParameters.WorkArea;
            MinWidth = 880;
            MinHeight = 480;
            Width = Math.Clamp(workArea.Width * 0.9, MinWidth, 1440);
            Height = Math.Clamp(workArea.Height * 0.9, MinHeight, 900);
            stage.Focusable = true;
            System.Windows.Automation.AutomationProperties.SetName(stage, "图片预览");
            stage.PreviewMouseLeftButtonDown += (_, _) => stage.Focus();
            FocusManager.SetFocusedElement(this, stage);
            bool initialFocusApplied = false;
            void FocusPreviewOnOpen()
            {
                if (initialFocusApplied || !IsLoaded || !IsActive) return;
                initialFocusApplied = stage.IsKeyboardFocusWithin || stage.Focus();
            }
            Activated += (_, _) => FocusPreviewOnOpen();
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(FocusPreviewOnOpen), DispatcherPriority.Input);
        }

        Loaded += async (_, _) =>
        {
            FitToWindow();
            await LoadAnnotationDictionaryAsync();
            if (!lifetime.IsCancellationRequested) await ShowCurrentAsync();
        };
        previousArrow.Click += async (_, _) => await MoveAsync(-1);
        nextArrow.Click += async (_, _) => await MoveAsync(1);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            e.Handled = true;
            await MoveAsync(e.Key == Key.Left ? -1 : 1);
        };
        PreviewKeyDown += async (_, e) =>
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.C && Keyboard.FocusedElement is not TextBox
                && (annotation is null || stage.IsKeyboardFocusWithin || annotationList?.IsKeyboardFocusWithin == true))
            {
                e.Handled = true;
                await RunImageActionAsync(images[index], openInPaint: false);
            }
            else if (e.Key == Key.Escape && fullscreen) { e.Handled = true; ToggleFullscreen(); }
            else if (e.Key == Key.F11) { e.Handled = true; ToggleFullscreen(); }
            else if (ctrl && e.Key is Key.OemPlus or Key.Add) { e.Handled = true; ZoomAtCenter(zoom * 1.15); }
            else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; ZoomAtCenter(zoom / 1.15); }
        };
        Closing += (_, e) => { e.Cancel = annotationWriting || !ConfirmDiscard("关闭窗口"); };
        Closed += (_, _) => { lifetime.Cancel(); loadCancel?.Cancel(); thumbScrollTimer.Stop(); annotationPanel?.Dispose(); };
        UpdateZoomUi();
    }

    private ContextMenu BuildImageContextMenu()
    {
        var menu = new ContextMenu { MinWidth = 220 };
        RemoteEntry? target = null;
        MenuItem Item(string text, string geometry)
        {
            var glyph = new System.Windows.Shapes.Path { Data = (Geometry)FindResource(geometry), Style = ThemeStyle("IconGlyph") };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "InkBrush");
            return new MenuItem
            {
                Header = text, Icon = glyph, MinHeight = (double)FindResource("ToolbarControlHeight")
            };
        }
        var copy = Item("复制", "IconRestoreGeometry");
        copy.InputGestureText = "Ctrl+C";
        menu.Items.Add(copy);
        copy.Click += async (_, _) => await RunImageActionAsync(target ?? images[index], openInPaint: false);
        menu.Opened += (_, _) =>
        {
            target = images[index];
            copy.IsEnabled = image.Source is not null && !imageActionBusy;
        };
        return menu;
    }

    private async Task RunImageActionAsync(RemoteEntry entry, bool openInPaint)
    {
        if (imageActionBusy || lifetime.IsCancellationRequested) return;
        imageActionBusy = true;
        Toast.Show(this, "正在准备 " + entry.Name, ToastKind.Info, 1200);
        try
        {
            string file = await PrepareImageFileAsync(entry, lifetime.Token);
            if (openInPaint)
            {
                using var process = Process.Start(CreatePaintStartInfo(file));
            }
            else
            {
                var bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(file);
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    // 独立像素源可由 WPF 正确序列化为剪贴板 DIB，避免解码帧延迟导出失败。
                    var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    int stride = checked(converted.PixelWidth * 4);
                    var pixels = new byte[checked(stride * converted.PixelHeight)];
                    converted.CopyPixels(pixels, stride, 0);
                    var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
                        converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
                    result.Freeze();
                    return result;
                }, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                var data = new DataObject();
                data.SetImage(bitmap);
                data.SetFileDropList(new StringCollection { file });
                Clipboard.SetDataObject(data, true);
                Toast.Show(this, "已复制 " + entry.Name, ToastKind.Success);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "图片右键操作失败；路径 {RemotePath}，画图 {OpenInPaint}", entry.Path, openInPaint);
            Toast.Show(this, (openInPaint ? "打开画图失败：" : "复制失败：") + ex.Message, ToastKind.Danger, 4000);
        }
        finally { imageActionBusy = false; }
    }

    private async Task<string> PrepareImageFileAsync(RemoteEntry entry, CancellationToken ct)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ImageCache");
        Directory.CreateDirectory(root);
        foreach (string directory in Directory.GetDirectories(root))
        {
            try
            {
                if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) Directory.Delete(directory, true);
            }
            catch (Exception ex) { Log.Debug(ex, "清理旧图片缓存失败；路径 {ImageCache}", directory); }
        }
        string session = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string file = Path.Combine(session, Path.GetFileName(entry.Name));
        await client.DownloadFileAsync(entry.Path, file, ct: ct);
        ct.ThrowIfCancellationRequested();
        return file;
    }

    private static ProcessStartInfo CreatePaintStartInfo(string file)
    {
        var start = new ProcessStartInfo("mspaint.exe") { UseShellExecute = true };
        start.ArgumentList.Add(file);
        return start;
    }

    // ---------------- 标注模式：左栏图片列表 + 右栏标注面板 ----------------

    private Border BuildAnnotationList(Brush surface, Brush line, Brush muted, ColumnDefinition column)
    {
        var visibleImages = new ListCollectionView(thumbs);
        var list = new ListBox
        {
            ItemsSource = visibleImages,
            IsSynchronizedWithCurrentItem = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 6),
            ItemTemplate = BuildAnnotationItemTemplate(),
            ItemContainerStyle = BuildAnnotationItemStyle()
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += async (_, _) =>
        {
            if (syncingAnnotation || list.SelectedItem is not ImageThumbItem item) return;
            await MoveToAsync(item.Index);
            list.ScrollIntoView(item);
        };
        list.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) LoadAnnotationThumbs();
        };
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => LoadAnnotationThumbs()));
        annotationList = list;

        var head = new Grid { Height = 42 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "图片列表",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        int annotatedCount = thumbs.Count(item => item.Annotated);
        var count = new TextBlock
        {
            Text = $"已标注 {annotatedCount} / {thumbs.Count} 张",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = muted
        };
        if (annotatedCount > 0)
            count.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        head.Children.Add(title);
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        var toggle = CreateIconButton("IconChevronGeometry", "折叠图片列表", out var chevron);
        toggle.Margin = new Thickness(0, 0, 4, 0);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotation = new RotateTransform(90);
        GridLength expandedWidth = column.Width;
        chevron.RenderTransform = rotation;
        var filter = new CheckBox { Content = "仅看已标注", Margin = new Thickness(12, 0, 12, 8) };
        var empty = new TextBlock
        {
            Text = "本批次暂无已标注图片",
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8, 12, 0),
            Visibility = Visibility.Collapsed
        };
        filter.Click += async (_, _) =>
        {
            if (annotationWriting)
            {
                filter.IsChecked = onlyAnnotated;
                return;
            }
            bool enabled = filter.IsChecked == true;
            var firstAnnotated = enabled && !thumbs[index].Annotated ? thumbs.FirstOrDefault(item => item.Annotated) : null;
            if (firstAnnotated is not null && !ConfirmDiscard("切图"))
            {
                filter.IsChecked = onlyAnnotated;
                return;
            }
            onlyAnnotated = enabled;
            syncingAnnotation = true;
            try { visibleImages.Filter = enabled ? item => ((ImageThumbItem)item).Annotated : null; }
            finally { syncingAnnotation = false; }
            empty.Visibility = visibleImages.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            if (firstAnnotated is not null)
            {
                index = firstAnnotated.Index;
                await ShowCurrentAsync();
            }
            else
            {
                SyncAnnotationSelection();
                UpdateNavigationArrows();
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "折叠图片列表");
        toggle.Click += (_, _) =>
        {
            bool expand = list.Visibility == Visibility.Collapsed;
            column.Width = expand ? expandedWidth : new GridLength(40);
            list.Visibility = title.Visibility = count.Visibility = filter.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = expand && visibleImages.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            rotation.Angle = expand ? 90 : -90;
            string action = expand ? "折叠图片列表" : "展开图片列表";
            toggle.ToolTip = action;
            System.Windows.Automation.AutomationProperties.SetName(toggle, action);
            if (expand && list.SelectedItem is { } selected) list.ScrollIntoView(selected);
        };
        Grid.SetColumn(toggle, 2);
        head.Children.Add(toggle);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(filter);
        root.Children.Add(empty);
        root.Children.Add(list);
        return new Border { Background = surface, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("PanelCornerRadius"), Child = root };
    }

    private DataTemplate BuildAnnotationItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(FrameworkElement.HeightProperty, 56.0);

        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.SetValue(Border.WidthProperty, 40.0);
        thumb.SetValue(Border.HeightProperty, 40.0);
        thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        thumb.SetValue(Border.MarginProperty, new Thickness(4, 0, 8, 0));
        thumb.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(DockPanel.DockProperty, Dock.Left);
        thumb.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x36)));
        thumb.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x36, 0x3C, 0x43)));
        thumb.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(ImageThumbItem.Thumbnail)));
        thumb.AppendChild(image);
        root.AppendChild(thumb);

        var badge = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        badge.SetValue(FrameworkElement.WidthProperty, 6.0);
        badge.SetValue(FrameworkElement.HeightProperty, 6.0);
        badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(DockPanel.DockProperty, Dock.Right);
        badge.SetValue(System.Windows.Shapes.Shape.FillProperty, new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)));
        badge.SetValue(ToolTipService.ToolTipProperty, "已标注");
        badge.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ImageThumbItem.BadgeVisibility)));
        root.AppendChild(badge);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        var caption = new FrameworkElementFactory(typeof(TextBlock), "ImageCaption");
        caption.SetValue(TextBlock.FontSizeProperty, 11.0);
        caption.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        caption.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImageThumbItem.Caption)));
        caption.SetBinding(ToolTipService.ToolTipProperty, new Binding(nameof(ImageThumbItem.Caption)));
        stack.AppendChild(caption);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetValue(TextBlock.FontSizeProperty, 10.0);
        detail.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        detail.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x7D, 0x86, 0x8F)));
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImageThumbItem.Detail)));
        stack.AppendChild(detail);
        root.AppendChild(stack);

        var template = new DataTemplate { VisualTree = root };
        var annotated = new DataTrigger { Binding = new Binding(nameof(ImageThumbItem.Annotated)), Value = true };
        annotated.Setters.Add(new Setter(TextBlock.ForegroundProperty, ThemeBrush("SuccessBrush", Color.FromRgb(0x4C, 0xC3, 0x8A)), "ImageCaption"));
        template.Triggers.Add(annotated);
        return template;
    }

    private Style BuildAnnotationItemStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2, 0, 0, 0));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        var hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ThemeBrush("HoverOverlayBrush", Colors.Transparent), "Bd"));
        template.Triggers.Add(hover);
        selected.Setters.Add(new Setter(Border.BackgroundProperty, ThemeBrush("AccentSoftBrush", Colors.Transparent), "Bd"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, ThemeBrush("AccentBrush", Colors.SteelBlue), "Bd"));
        template.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(2, 1, 2, 1)));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private Task LoadAnnotationDictionaryAsync() => annotationPanel?.LoadAsync() ?? Task.CompletedTask;
    private Task LoadAnnotationForCurrentAsync()
    {
        annotationPanel?.SelectImage(images[index]);
        return Task.CompletedTask;
    }

    private void LoadAnnotationThumbs()
    {
        if (annotationList is null) return;
        foreach (var item in thumbs)
        {
            if (item.Thumbnail is not null) continue;
            // 只加载已经实现出来的项，滚动到哪加载到哪。
            if (annotationList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem) continue;
            EnsureThumb(item);
        }
    }

    private void SyncAnnotationSelection()
    {
        if (annotationList is null) return;
        syncingAnnotation = true;
        try
        {
            var current = thumbs[index];
            bool visible = !onlyAnnotated || current.Annotated;
            annotationList.SelectedItem = visible ? current : null;
            if (visible) annotationList.ScrollIntoView(current);
        }
        finally { syncingAnnotation = false; }
    }

    private void UseDarkTheme()
    {
        // 查看器始终使用深色主题，和 Windows 照片一致；不影响主程序的主题设置。
        string assembly = typeof(ImageViewer).Assembly.GetName().Name!;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assembly};component/Theme/TokensDark.xaml", UriKind.Absolute)
        });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assembly};component/Theme/Controls.xaml", UriKind.Absolute)
        });
    }

    private Button CreateTitleButton(string geometryKey, string tooltip, string styleKey = "ViewerCaptionButton")
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(geometryKey),
            Style = ThemeStyle("IconGlyph"),
            SnapsToDevicePixels = true
        };
        var button = new Button
        {
            Content = icon,
            ToolTip = tooltip,
            Style = ThemeStyle(styleKey)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private Button CreateIconButton(string geometryKey, string tooltip, out System.Windows.Shapes.Path icon)
    {
        icon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(geometryKey),
            Style = ThemeStyle("IconGlyph"),
            SnapsToDevicePixels = true
        };
        var button = new Button
        {
            Content = icon,
            ToolTip = tooltip,
            Foreground = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC))
        };
        var style = ThemeStyle("IconButton");
        if (style is not null) button.Style = style;
        return button;
    }

    private Border CreateInfoSeparator() => new()
    {
        Width = 1,
        Height = 14,
        Margin = new Thickness(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = ThemeBrush("LineBrush", Color.FromRgb(0x36, 0x3C, 0x43))
    };

    private System.Windows.Shapes.Path CreateInfoIcon(string geometryKey) => new()
    {
        Data = (Geometry)FindResource(geometryKey),
        Style = ThemeStyle("SmallIconGlyph"),
        Stroke = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC)),
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private Button CreateArrow(bool right, string caption, HorizontalAlignment alignment)
    {
        // 用同色描边 + 圆角连接，把三角的三个尖角磨圆。
        var triangle = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(right ? "M0,0 L9,7 L0,14 Z" : "M9,0 L0,7 L9,14 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xE8)),
            Stroke = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xE8)),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 9,
            Height = 14,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        var pill = new Border
        {
            Width = 26,
            Height = 68,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x32, 0x36, 0x3A)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = triangle
        };
        // 指针在箭头上停留约 0.8 秒后才浮出“上一张/下一张”，和 Windows 照片一致放在胶囊正下方。
        var label = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = caption,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xAF, 0xB6)),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(right ? 0 : 8, 0, right ? 8 : 0, 0)
        };
        content.Children.Add(pill);
        content.Children.Add(label);
        // 可见按钮很小，但热区覆盖整条边缘并贯通整个高度，和 Windows 照片一致。
        var button = new Button
        {
            Content = content,
            Width = 124,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Opacity = 0,
            Cursor = Cursors.Hand,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        var template = new ControlTemplate(typeof(Button));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        button.Template = template;
        System.Windows.Automation.AutomationProperties.SetName(button, right ? "下一张图片" : "上一张图片");

        var scale = (ScaleTransform)pill.RenderTransform;
        void Press(bool down)
        {
            var duration = TimeSpan.FromMilliseconds(down ? 70 : 140);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(down ? 0.9 : 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(down ? 0.9 : 1, duration) { EasingFunction = ease });
        }
        static void Fade(UIElement element, double to, int milliseconds)
            => element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        var labelDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        labelDelay.Tick += (_, _) => { labelDelay.Stop(); Fade(label, 1, 120); };
        button.MouseEnter += (_, _) =>
        {
            if (!button.IsEnabled) return;
            SetArrowVisual(button, 1, 90);
            labelDelay.Start();
        };
        button.MouseLeave += (_, _) =>
        {
            labelDelay.Stop();
            Fade(label, 0, 100);
            Press(false);
        };
        button.PreviewMouseLeftButtonDown += (_, _) => Press(true);
        button.PreviewMouseLeftButtonUp += (_, _) => Press(false);
        return button;
    }

    // 指针在图片区域内就保持两侧箭头可见：靠近边缘提亮到实体，离开窗口 0.9 秒后淡出。
    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        UpdatePixelPosition(e.GetPosition(image));
        var point = e.GetPosition(stage);
        double zone = Math.Clamp(stage.ActualWidth * 0.16, 96, 220);
        RevealArrow(previousArrow, point.X <= zone);
        RevealArrow(nextArrow, point.X >= stage.ActualWidth - zone);
    }

    private void Stage_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdatePixelPosition(e.GetPosition(image));
        arrowHideTimer.Stop();
        SetArrowVisual(previousArrow, previousArrow.IsEnabled ? ArrowDimOpacity : 0);
        SetArrowVisual(nextArrow, nextArrow.IsEnabled ? ArrowDimOpacity : 0);
    }

    private void Stage_MouseLeave(object sender, MouseEventArgs e)
    {
        pixelNumbers.Text = "—, —";
        arrowHideTimer.Stop();
        arrowHideTimer.Start();
    }

    private void UpdatePixelPosition(Point point)
    {
        if (image.Source is BitmapSource bitmap
            && point.X >= 0 && point.Y >= 0 && point.X < bitmap.PixelWidth && point.Y < bitmap.PixelHeight)
        {
            pixelNumbers.Text = $"{(int)point.X}, {(int)point.Y}";
        }
        else pixelNumbers.Text = "—, —";
    }

    private void RevealArrow(Button arrow, bool near)
    {
        if (!arrow.IsEnabled) { SetArrowVisual(arrow, 0); return; }
        SetArrowVisual(arrow, near ? 1 : ArrowDimOpacity);
        if (near) arrowHideTimer.Stop();
    }

    private void SetArrowVisual(Button arrow, double target, int milliseconds = 120)
    {
        if (milliseconds <= 0)
        {
            arrow.BeginAnimation(OpacityProperty, null);
            arrow.Opacity = target;
            return;
        }
        arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ToggleThumbnails()
    {
        bool show = thumbBar.Visibility != Visibility.Visible;
        thumbBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        thumbToggle.ToolTip = show ? "隐藏缩略图" : "显示缩略图";
        if (show)
        {
            SyncThumbSelection();
            EnsureThumbsAround(index);
        }
    }

    private void ShowZoomPresets(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (int percent in new[] { 800, 700, 600, 500, 400, 300, 200, 100, 75, 50, 25, 10 })
        {
            var item = new MenuItem { Header = percent + "%" };
            item.Click += (_, _) => { fitMode = false; ZoomAtCenter(percent / 100d); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ApplyPercentText()
    {
        string text = percentBox.Text.Trim().TrimEnd('%', ' ');
        if (double.TryParse(text, out double percent)) { fitMode = false; ZoomAtCenter(Math.Clamp(percent, 5, 800) / 100d); }
        else UpdateZoomUi();
    }

    private void UpdateZoomUi()
    {
        updatingZoom = true;
        try
        {
            int percent = (int)Math.Round(zoom * 100);
            if (!percentBox.IsKeyboardFocused) percentBox.Text = percent + "%";
            zoomSlider.Value = Math.Clamp(percent, 10, 800);
            // 实际大小/适应窗口可来回切换，按钮始终可点，不因到达 100% 而禁用。
            bool atActual = Math.Abs(zoom - 1) < 0.01;
            actualSizeButton.ToolTip = atActual ? "适应窗口" : "缩放到实际大小 (100%)";
            actualSizeIcon.Data = (Geometry)FindResource(atActual ? "IconFitGeometry" : "IconActualSizeGeometry");
        }
        finally { updatingZoom = false; }
    }

    private void SetZoom(double value, bool center)
    {
        ClearZoomAnimations();
        zoom = Math.Clamp(value, 0.05, 8);
        scale.ScaleX = scale.ScaleY = zoom;
        if (center) CenterImage();
        UpdateZoomUi();
        UpdateCursor();
    }

    private void ClearZoomAnimations()
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void CenterImage()
    {
        if (image.Source is not BitmapSource bitmap) return;
        translate.X = (canvas.ActualWidth - bitmap.PixelWidth * zoom) / 2;
        translate.Y = (canvas.ActualHeight - bitmap.PixelHeight * zoom) / 2;
    }

    private void ToggleActualSize()
    {
        // 100% 时回到适应窗口；其他比例时切到实际大小。
        if (Math.Abs(zoom - 1) < 0.01) FitToWindow();
        else { fitMode = false; SetZoom(1, center: true); }
    }

    private double FitZoom()
    {
        if (image.Source is not BitmapSource bitmap || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return 1;
        const double sideInset = 3;
        double availableWidth = canvas.ActualWidth - 2 * sideInset;
        double fit = Math.Min(availableWidth / bitmap.PixelWidth, canvas.ActualHeight / bitmap.PixelHeight);
        return Math.Clamp(Math.Min(fit, 1.0), 0.05, 8);
    }

    private void FitToWindow()
    {
        if (image.Source is null) return;
        fitMode = true;
        SetZoom(FitZoom(), center: true);
    }

    private void ZoomAtCenter(double value, bool animate = true) => ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), value, animate);

    private void ZoomAt(Point anchor, double value, bool animate = true)
    {
        if (image.Source is null) return;
        double target = Math.Clamp(value, 0.05, 8);
        if (Math.Abs(target - zoom) < 0.0001) return;
        // 用当前实际渲染值（可能正在动画中）计算锚点，连续滚轮不会跳变。
        double current = scale.ScaleX;
        double imageX = (anchor.X - translate.X) / current;
        double imageY = (anchor.Y - translate.Y) / current;
        double targetX = anchor.X - imageX * target;
        double targetY = anchor.Y - imageY * target;
        zoom = target;
        fitMode = false;
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, duration) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(targetX, duration) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, duration) { EasingFunction = ease });
        }
        else
        {
            ClearZoomAnimations();
            scale.ScaleX = scale.ScaleY = target;
            translate.X = targetX;
            translate.Y = targetY;
        }
        UpdateZoomUi();
        UpdateCursor();
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (image.Source is null) return;
        double fit = FitZoom();
        // 滚轮缩小到适应窗口就停住；要继续缩小请点减号。
        if (e.Delta < 0 && zoom <= fit + 0.0001) { e.Handled = true; return; }
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double target = zoom * factor;
        if (e.Delta < 0) target = Math.Max(target, fit);
        ZoomAt(e.GetPosition(canvas), target);
        e.Handled = true;
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            if (Math.Abs(zoom - 1) < 0.01) FitToWindow();
            else { fitMode = false; SetZoom(1, center: true); }
            return;
        }
        if (image.Source is null) return;
        // 只有图片超出画布时才允许拖动，适应窗口状态下保持稳定。
        if (image.Source is BitmapSource bitmap && bitmap.PixelWidth * zoom <= canvas.ActualWidth + 1 && bitmap.PixelHeight * zoom <= canvas.ActualHeight + 1) return;
        dragging = true;
        dragStart = e.GetPosition(canvas);
        dragOriginX = translate.X;
        dragOriginY = translate.Y;
        canvas.CaptureMouse();
        canvas.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        Point point = e.GetPosition(canvas);
        translate.X = dragOriginX + point.X - dragStart.X;
        translate.Y = dragOriginY + point.Y - dragStart.Y;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        dragging = false;
        canvas.ReleaseMouseCapture();
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (image.Source is not BitmapSource bitmap) { canvas.Cursor = Cursors.Arrow; return; }
        bool overflows = bitmap.PixelWidth * zoom > canvas.ActualWidth + 1 || bitmap.PixelHeight * zoom > canvas.ActualHeight + 1;
        canvas.Cursor = overflows ? Cursors.Hand : Cursors.Arrow;
    }

    private void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        titleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (fullscreen)
        {
            WindowState = WindowState.Maximized;
            fullscreenButton.ToolTip = "退出全屏 (Esc)";
            fullscreenIcon.Data = (Geometry)FindResource("IconCollapseGeometry");
        }
        else
        {
            WindowState = WindowState.Normal;
            fullscreenButton.ToolTip = "全屏 (F11)";
            fullscreenIcon.Data = (Geometry)FindResource("IconExpandGeometry");
        }
    }

    private bool ConfirmDiscard(string action)
    {
        if (annotation is null || !annotationDirty) return true;
        if (!Dialog.Confirm(this, $"当前图片的标注尚未提交，{action}后填写的内容会丢失。是否继续？", "未提交的标注")) return false;
        annotationPanel?.Model.DiscardDraft();
        return true;
    }

    public async Task MoveAsync(int direction)
    {
        int target = FindAdjacentImage(direction);
        if (target >= 0) await MoveToAsync(target);
    }

    private int FindAdjacentImage(int direction)
    {
        if (direction == 0) return -1;
        for (int target = index + direction; target >= 0 && target < images.Length; target += Math.Sign(direction))
            if (!onlyAnnotated || thumbs[target].Annotated) return target;
        return -1;
    }

    private void UpdateNavigationArrows()
    {
        previousArrow.IsEnabled = FindAdjacentImage(-1) >= 0;
        nextArrow.IsEnabled = FindAdjacentImage(1) >= 0;
        if (!previousArrow.IsEnabled) SetArrowVisual(previousArrow, 0, 0);
        if (!nextArrow.IsEnabled) SetArrowVisual(nextArrow, 0, 0);
    }

    public async Task MoveToAsync(int target)
    {
        if (annotationWriting) { SyncAnnotationSelection(); SyncThumbSelection(); return; }
        if (target < 0 || target >= images.Length) return;
        if (target == index) { SyncThumbSelection(); return; }
        if (!ConfirmDiscard("切图")) { SyncAnnotationSelection(); SyncThumbSelection(); return; }
        index = target;
        await ShowCurrentAsync();
    }

    private void SyncThumbSelection()
    {
        thumbScrollTimer.Stop();
        syncingThumb = true;
        try
        {
            thumbList.SelectedIndex = index;
            if (index >= 0 && index < thumbs.Count) thumbList.ScrollIntoView(thumbs[index]);
        }
        finally { syncingThumb = false; }
    }

    private async void Thumb_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingThumb || thumbList.SelectedIndex < 0) return;
        await MoveToAsync(thumbList.SelectedIndex);
    }

    private void Thumb_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentWidth > 0) LoadRealizedThumbs();
    }

    private void Thumb_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        thumbScrollViewer ??= FindVisualChild<ScrollViewer>(thumbList);
        if (thumbScrollViewer is null) return;
        double step = e.Delta / 120d * 180d;
        StartThumbScroll(thumbScrollViewer.HorizontalOffset - step);
        e.Handled = true;
    }

    private void StartThumbScroll(double target)
    {
        if (thumbScrollViewer is null) return;
        thumbScrollFrom = thumbScrollViewer.HorizontalOffset;
        thumbScrollTo = Math.Clamp(target, 0, thumbScrollViewer.ScrollableWidth);
        thumbScrollElapsed = 0;
        thumbScrollTimer.Stop();
        thumbScrollTimer.Start();
    }

    private void TickThumbScroll()
    {
        if (thumbScrollViewer is null) { thumbScrollTimer.Stop(); return; }
        thumbScrollElapsed += 15;
        double t = Math.Min(1, thumbScrollElapsed / (double)ThumbScrollDuration);
        double eased = 1 - Math.Pow(1 - t, 3);
        thumbScrollViewer.ScrollToHorizontalOffset(thumbScrollFrom + (thumbScrollTo - thumbScrollFrom) * eased);
        if (t >= 1) thumbScrollTimer.Stop();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void LoadRealizedThumbs()
    {
        foreach (var container in FindVisualChildren<ListBoxItem>(thumbList))
            if (container.DataContext is ImageThumbItem item) EnsureThumb(item);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void EnsureThumbsAround(int center) => EnsureThumbs(Math.Max(0, center - 6), center + 14);

    private void EnsureThumbs(int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(thumbs.Count - 1, end);
        for (int i = start; i <= end; i++) EnsureThumb(thumbs[i]);
    }

    private void EnsureThumb(ImageThumbItem item)
    {
        if (item.Thumbnail is not null) return;
        if (imageCache.TryGetValue(item.Path, out var full))
        {
            var thumb = CreateThumb(full);
            thumbCache[item.Path] = thumb;
            item.Thumbnail = thumb;
            return;
        }
        if (thumbCache.TryGetValue(item.Path, out var cached)) { item.Thumbnail = cached; return; }
        if (!thumbLoading.Add(item.Path)) return;
        item.Failed = false;
        _ = LoadThumbAsync(item);
    }

    private async Task LoadThumbAsync(ImageThumbItem item)
    {
        try
        {
            await thumbSlots.WaitAsync(lifetime.Token);
            try
            {
                if (item.Thumbnail is not null) return;
                byte[] bytes = await client.ReadImageAsync(item.Path, lifetime.Token);
                var bitmap = await Task.Run(() => DecodeThumb(bytes), lifetime.Token);
                thumbCache[item.Path] = bitmap;
                item.Thumbnail = bitmap;
            }
            finally { thumbSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug(ex, "缩略图加载失败；路径 {Path}", item.Path); item.Failed = true; }
        finally { thumbLoading.Remove(item.Path); }
    }

    private static BitmapSource DecodeThumb(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 160;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateThumb(BitmapSource source)
    {
        double scale = Math.Min(1d, 160d / Math.Max(1, source.PixelWidth));
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static DataTemplate BuildThumbTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.WidthProperty, 104.0);
        grid.SetValue(FrameworkElement.HeightProperty, 64.0);
        grid.SetValue(Panel.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x18)));
        grid.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ImageThumbItem.Tip)));
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(ImageThumbItem.Thumbnail)));
        grid.AppendChild(image);
        var placeholder = new FrameworkElementFactory(typeof(TextBlock), "Placeholder");
        placeholder.SetValue(TextBlock.TextProperty, "…");
        placeholder.SetValue(TextBlock.FontSizeProperty, 16.0);
        placeholder.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x78)));
        placeholder.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        placeholder.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        placeholder.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(placeholder);
        var failed = new FrameworkElementFactory(typeof(TextBlock), "Failed");
        failed.SetValue(TextBlock.TextProperty, "×");
        failed.SetValue(TextBlock.FontSizeProperty, 14.0);
        failed.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x5E)));
        failed.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        failed.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        failed.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 0, 0));
        failed.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(failed);
        var template = new DataTemplate { VisualTree = grid };
        var noThumb = new DataTrigger { Binding = new Binding(nameof(ImageThumbItem.HasThumbnail)), Value = false };
        noThumb.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Placeholder"));
        template.Triggers.Add(noThumb);
        var isFailed = new DataTrigger { Binding = new Binding(nameof(ImageThumbItem.Failed)), Value = true };
        isFailed.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Failed"));
        template.Triggers.Add(isFailed);
        return template;
    }

    private Style BuildThumbItemStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new Thickness(0));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), "Bd"));
        template.Triggers.Add(selected);
        var hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x66, 0x6C, 0x72)), "Bd"));
        template.Triggers.Add(hover);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(4, 0, 0, 0)));
        style.Setters.Add(new Setter(Control.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        return style;
    }

    private async Task ShowCurrentAsync()
    {
        pixelNumbers.Text = "—, —";
        SyncAnnotationSelection();
        await LoadAnnotationForCurrentAsync();
        var previousCancel = loadCancel;
        loadCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previousCancel?.Cancel();
        previousCancel?.Dispose();
        var token = loadCancel.Token;
        int version = ++loadVersion;
        previousArrow.IsEnabled = nextArrow.IsEnabled = false;
        SyncThumbSelection();
        EnsureThumbsAround(index);
        titleText.Text = images[index].Name;
        titleText.ToolTip = CurrentPath;
        Title = images[index].Name + " - 图片查看";
        // 切换图片时回到适应窗口，避免上一张的缩放比例影响下一张。
        fitMode = true;
        if (image.Source is null)
        {
            stageMessage.Text = "正在读取图片…";
            stageMessage.Visibility = Visibility.Visible;
        }
        try
        {
            var bitmap = await LoadImageAsync(CurrentPath, token);
            if (version != loadVersion) return;
            image.Source = bitmap;
            image.Width = bitmap.PixelWidth;
            image.Height = bitmap.PixelHeight;
            stageMessage.Visibility = Visibility.Collapsed;
            FitToWindow();
            UpdateInfo(bitmap);
            StartPrefetch();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || lifetime.IsCancellationRequested || version != loadVersion) { }
        catch (Exception ex)
        {
            Log.Error(ex, "图片显示失败；路径 {RemotePath}", CurrentPath);
            formatText.Text = "图片读取失败：" + ex.Message;
            dimensionText.Text = "";
            sizeText.Text = "";
            stageMessage.Text = "图片读取失败";
            stageMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            if (version == loadVersion) UpdateNavigationArrows();
        }
    }

    private void UpdateInfo(BitmapSource bitmap)
    {
        formatText.Text = Path.GetExtension(CurrentPath).TrimStart('.').ToUpperInvariant();
        dimensionText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        sizeText.Text = images[index].Length > 0 ? FormatSize(images[index].Length) : "";
        infoPanel.ToolTip = $"{images[index].Name}\n{annotation?.DisplayPath ?? CurrentPath}\n创建：{images[index].CreatedLocal:yyyy.MM.dd HH:mm}";
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        // 只对超大图片做降采样，避免小图被 DecodePixelWidth 放大导致尺寸和实际大小错误。
        using var probe = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int width = decoder.Frames[0].PixelWidth;
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 2400) bitmap.DecodePixelWidth = 2400;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void CacheImage(string path, BitmapSource bitmap)
    {
        if (imageCache.Remove(path, out var old)) cacheBytes -= Estimate(old);
        imageCache[path] = bitmap;
        cacheOrder.Remove(path);
        cacheOrder.AddFirst(path);
        cacheBytes += Estimate(bitmap);
        while (cacheBytes > CacheLimitBytes && cacheOrder.Last is { } oldest)
        {
            cacheOrder.RemoveLast();
            if (imageCache.Remove(oldest.Value, out var evicted)) cacheBytes -= Estimate(evicted);
        }
    }

    private void TouchCache(string path)
    {
        cacheOrder.Remove(path);
        cacheOrder.AddFirst(path);
    }

    private static long Estimate(BitmapSource bitmap) => bitmap.PixelWidth * (long)bitmap.PixelHeight * 4;

    // 命中缓存直接返回；正在预取的图片复用同一个请求，避免重复下载和取消日志。
    private async Task<BitmapSource> LoadImageAsync(string path, CancellationToken ct)
    {
        if (imageCache.TryGetValue(path, out var cached)) { TouchCache(path); return cached; }
        if (prefetchTask is { } task && prefetchPath == path)
        {
            if (await task is { } prefetched) return prefetched;
        }
        byte[] bytes = await client.ReadImageAsync(path, ct);
        var bitmap = await Task.Run(() => Decode(bytes), ct);
        CacheImage(path, bitmap);
        return bitmap;
    }

    private void StartPrefetch()
    {
        if (prefetchTask is { IsCompleted: false }) return;
        foreach (int offset in new[] { 1, -1 })
        {
            int target = index + offset;
            if (target < 0 || target >= images.Length) continue;
            string path = images[target].Path;
            if (imageCache.ContainsKey(path)) continue;
            prefetchPath = path;
            prefetchTask = PrefetchOneAsync(path, lifetime.Token);
            return;
        }
    }

    private async Task<BitmapSource?> PrefetchOneAsync(string path, CancellationToken token)
    {
        try
        {
            byte[] bytes = await client.ReadImageAsync(path, token);
            var bitmap = await Task.Run(() => Decode(bytes), token);
            CacheImage(path, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Debug(ex, "图片预取失败；路径 {RemotePath}", path); return null; }
        finally
        {
            if (prefetchPath == path) { prefetchTask = null; prefetchPath = null; }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024d * 1024) return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024d * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / (1024d * 1024 * 1024):N1} GB";
    }

    private Brush ThemeBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private Style? ThemeStyle(string key) => TryFindResource(key) as Style;
}

public sealed class ImageThumbItem(int index, RemoteEntry entry, int number) : INotifyPropertyChanged
{
    private BitmapSource? thumbnail;
    private bool failed;
    private bool annotated;
    public int Index { get; } = index;
    public string Path { get; } = entry.Path;
    public int Number { get; } = number;
    public string Tip { get; } = $"{number}. {entry.Name}\r\n{entry.CreatedLocal:yyyy.MM.dd HH:mm}\r\n{SizeText(entry.Length)}";
    /// <summary>标注模式左栏的两行文字。</summary>
    public string Caption => $"{Number:00}_{entry.Name}";
    public string Detail => $"{SizeText(entry.Length)} · {entry.ModifiedLocal:MM-dd HH:mm}".TrimStart(' ', '·');
    /// <summary>标注模式左栏的「已标注」角标。</summary>
    public bool Annotated
    {
        get => annotated;
        set
        {
            if (annotated == value) return;
            annotated = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Annotated)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeVisibility)));
        }
    }
    public Visibility BadgeVisibility => annotated ? Visibility.Visible : Visibility.Collapsed;
    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            if (ReferenceEquals(thumbnail, value)) return;
            thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        }
    }
    public bool HasThumbnail => thumbnail is not null;
    public bool Failed
    {
        get => failed;
        set
        {
            if (failed == value) return;
            failed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Failed)));
        }
    }
    private static string SizeText(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024d * 1024) return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024d * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / (1024d * 1024 * 1024):N1} GB";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
