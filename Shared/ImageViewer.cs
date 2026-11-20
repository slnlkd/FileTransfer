using System.IO;
using System.Text;
using System.Text.Json;
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
using Transfer.Core;
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
    private readonly TextBlock titleText = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(150, 0, 150, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly AnnotationContext? annotation;
    private ListBox? annotationList;
    private bool annotationDirty;
    private bool annotationWriting;
    private bool dictionaryAdding;
    private bool loadingAnnotation;
    private RemoteDictionary? dictionary;
    private string categoryCode = "";
    private string planCode = "";
    private readonly string operatorName = LoadOperatorName();
    private Button? categoryPicker;
    private Button? planPicker;
    private Button? submitButton;
    private Button? revokeButton;
    private TextBlock? categoryLabel;
    private TextBlock? planLabel;
    private TextBlock? annotationStatus;
    private Border? annotationStatusBadge;
    private TextBlock? annotationImageName;
    private TextBlock? annotationSummary;
    private TextBlock? annotationError;
    private TextBlock? annotationHint;
    private TextBox? noteBox;
    private TextBox? operatorBox;
    private System.Windows.Controls.Primitives.Popup? pickerPopup;
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
            CaptionHeight = 44,
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
        const double annotationListWidth = 244;

        // 标题栏：文件名居中，窗口按钮在右侧，统一使用线性图标。
        var titleButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var minimize = CreateTitleButton("IconMinimizeGeometry", "最小化");
        var maximize = CreateTitleButton("IconMaximizeGeometry", "最大化 / 还原");
        var close = CreateTitleButton("IconCloseGeometry", "关闭");
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
        var titleGrid = new Grid();
        titleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (annotation is not null)
        {
            titleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var pathLabel = new TextBlock
            {
                Text = annotation.DisplayPath,
                ToolTip = annotation.DisplayPath,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24, 0, 24, 8),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(pathLabel, 1);
            Grid.SetColumnSpan(pathLabel, 2);
            titleGrid.Children.Add(pathLabel);
            WindowChrome.SetIsHitTestVisibleInChrome(pathLabel, true);
            WindowChrome.SetIsHitTestVisibleInChrome(titleText, true);
            var defaultTitleMargin = titleText.Margin;
            titleGrid.SizeChanged += (_, e) =>
            {
                if (!e.WidthChanged) return;
                var pathText = new FormattedText(pathLabel.Text, System.Globalization.CultureInfo.CurrentUICulture,
                    pathLabel.FlowDirection, new Typeface(pathLabel.FontFamily, pathLabel.FontStyle, pathLabel.FontWeight, pathLabel.FontStretch),
                    pathLabel.FontSize, pathLabel.Foreground, VisualTreeHelper.GetDpi(pathLabel).PixelsPerDip);
                double reserve = Math.Max(defaultTitleMargin.Left, Math.Ceiling(pathText.WidthIncludingTrailingWhitespace) + 40);
                bool inline = reserve <= e.NewSize.Width / 3;
                Grid.SetRow(pathLabel, inline ? 0 : 1);
                pathLabel.Margin = inline ? new Thickness(24, 0, 0, 0) : new Thickness(24, 0, 24, 8);
                titleText.Margin = inline ? new Thickness(reserve, 0, reserve, 0) : defaultTitleMargin;
            };
        }
        Grid.SetColumnSpan(titleText, 2);
        titleGrid.Children.Add(titleText);
        Grid.SetColumn(titleButtons, 1);
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
        stage.Background = Background;
        stage.ClipToBounds = true;
        stage.Children.Add(canvas);
        stage.Children.Add(stageMessage);
        stage.Children.Add(previousArrow);
        stage.Children.Add(nextArrow);
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
        // 先停靠状态栏，再停靠缩略图条，最后让舞台填充剩余空间。
        panel.Children.Add(thumbBar);
        panel.Children.Add(stage);

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
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(annotationListWidth) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            var panelLine = ThemeBrush("HairlineBrush", Color.FromRgb(0x2C, 0x31, 0x37));
            columns.Children.Add(BuildAnnotationList(surface, panelLine, muted));
            panel.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(panel, 1);
            columns.Children.Add(panel);
            var annotationSide = BuildAnnotationSide(panelLine);
            Grid.SetColumn(annotationSide, 2);
            columns.Children.Add(annotationSide);
            shell.Children.Add(columns);
            Content = shell;
            // 标注模式下左栏已经承担了切换图片的职责，底部胶片条和它的分隔线一起隐藏。
            thumbToggle.Visibility = Visibility.Collapsed;
            thumbBar.Visibility = Visibility.Collapsed;
            if (infoPanel.Children.Count > 0 && infoPanel.Children[0] is Border) infoPanel.Children.RemoveAt(0);
            // 按屏幕可用区域自适应，不强行放大到 1440×900（高 DPI 下会超出屏幕）。
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(1000, Math.Min(1440, workArea.Width * 0.9));
            Height = Math.Max(600, Math.Min(900, workArea.Height * 0.9));
            MinWidth = 960;
            MinHeight = 560;
        }

        Loaded += async (_, _) => { FitToWindow(); await LoadAnnotationDictionaryAsync(); await ShowCurrentAsync(); };
        previousArrow.Click += async (_, _) => await MoveAsync(-1);
        nextArrow.Click += async (_, _) => await MoveAsync(1);
        PreviewKeyDown += async (_, e) =>
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key is Key.Left or Key.Right) { e.Handled = true; await MoveAsync(e.Key == Key.Left ? -1 : 1); }
            else if (e.Key == Key.Escape && fullscreen) { e.Handled = true; ToggleFullscreen(); }
            else if (e.Key == Key.F11) { e.Handled = true; ToggleFullscreen(); }
            else if (ctrl && e.Key is Key.OemPlus or Key.Add) { e.Handled = true; ZoomAtCenter(zoom * 1.15); }
            else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; ZoomAtCenter(zoom / 1.15); }
        };
        Closing += (_, e) => { if (annotationWriting) e.Cancel = true; };
        Closed += (_, _) => { lifetime.Cancel(); loadCancel?.Cancel(); thumbScrollTimer.Stop(); if (pickerPopup is not null) pickerPopup.IsOpen = false; };
        UpdateZoomUi();
    }

    // ---------------- 标注模式：左栏图片列表 + 右栏标注面板 ----------------

    private Border BuildAnnotationList(Brush surface, Brush line, Brush muted)
    {
        var list = new ListBox
        {
            ItemsSource = thumbs,
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
        var title = new TextBlock
        {
            Text = "图片列表",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var count = new TextBlock
        {
            Text = $"{thumbs.Count} 张",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = muted
        };
        head.Children.Add(title);
        Grid.SetColumn(count, 1);
        head.Children.Add(count);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(list);
        return new Border { Background = surface, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = root };
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

    private Border BuildAnnotationSide(Brush line)
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        var title = new TextBlock { Text = "异常标注", FontSize = 15, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        stack.Children.Add(title);

        stack.Children.Add(BuildAnnotationStatusCard(line));

        stack.Children.Add(FieldLabel("异常归类", 16));
        (categoryPicker, categoryLabel) = CreatePicker("请选择异常归类");
        stack.Children.Add(CreatePickerRow(categoryPicker, categoryLabel, AnnotationDictionaryKind.Category, "异常归类"));

        stack.Children.Add(FieldLabel("异常处理方案", 12));
        (planPicker, planLabel) = CreatePicker("请选择异常处理方案");
        stack.Children.Add(CreatePickerRow(planPicker, planLabel, AnnotationDictionaryKind.Plan, "异常处理方案"));

        stack.Children.Add(FieldLabel("备注（可选）", 12));
        noteBox = new TextBox
        {
            Height = 72,
            Style = ThemeStyle("RoundedTextInput"),
            Margin = new Thickness(0, 6, 0, 0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = AnnotationStore.NoteLimit
        };
        noteBox.TextChanged += (_, _) => MarkDirty();
        stack.Children.Add(noteBox);

        stack.Children.Add(FieldLabel("操作人", 12));
        operatorBox = new TextBox { Style = ThemeStyle("RoundedTextInput"), Margin = new Thickness(0, 6, 0, 0), Text = operatorName };
        operatorBox.TextChanged += (_, _) => MarkDirty();
        stack.Children.Add(operatorBox);

        submitButton = new Button
        {
            Content = "提交标注",
            Height = 34,
            MinWidth = 112,
            Style = ThemeStyle("PrimaryButton")
        };
        submitButton.SetResourceReference(Control.ForegroundProperty, "CanvasBrush");
        submitButton.Click += async (_, _) => await SubmitAnnotationAsync();
        revokeButton = new Button
        {
            Content = "撤销标注",
            ToolTip = "删除当前图片的全部标注记录",
            Height = 34,
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Style = ThemeStyle("DangerButton"),
            Visibility = Visibility.Collapsed
        };
        revokeButton.Click += async (_, _) => await RevokeAnnotationAsync();
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { revokeButton, submitButton }
        });

        annotationError = new TextBlock { FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        annotationError.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        stack.Children.Add(annotationError);

        annotationHint = new TextBlock { FontSize = 10, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        annotationHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
        stack.Children.Add(annotationHint);

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var side = new Border { BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = scroll };
        side.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return side;
    }

    private Border BuildAnnotationStatusCard(Brush line)
    {
        annotationStatus = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        annotationStatusBadge = new Border { Child = annotationStatus, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3) };
        var header = new DockPanel();
        DockPanel.SetDock(annotationStatusBadge, Dock.Right);
        header.Children.Add(annotationStatusBadge);
        var caption = FieldLabel("当前图片", 0);
        caption.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(caption);

        annotationImageName = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        annotationSummary = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed
        };
        annotationSummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var card = new Border
        {
            Child = new StackPanel { Children = { header, annotationImageName, annotationSummary } },
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6), BorderBrush = line, BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "HeaderBrush");
        return card;
    }

    private TextBlock FieldLabel(string text, double top)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, top, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return label;
    }

    // 用按钮 + 主题化菜单代替 ComboBox：暗色下原生 ComboBox 会露出浅色弹层。
    private (Button Button, TextBlock Label) CreatePicker(string placeholder)
    {
        var label = new TextBlock
        {
            Text = placeholder,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 18, 0)
        };
        var chevron = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("IconChevronGeometry"),
            Style = ThemeStyle("IconGlyph"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        var content = new Grid();
        content.Children.Add(label);
        content.Children.Add(chevron);
        var button = new Button
        {
            Content = content,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = placeholder
        };
        button.SetResourceReference(Control.BackgroundProperty, "HeaderBrush");
        button.Click += (_, _) => ShowPickerMenu(button, label, placeholder);
        return (button, label);
    }

    private Grid CreatePickerRow(Button picker, TextBlock label, AnnotationDictionaryKind kind, string fieldName)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add = new Button
        {
            Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconAddGeometry"), Style = ThemeStyle("IconGlyph") },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "新增" + fieldName
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "新增" + fieldName);
        add.Click += (_, _) => ShowDictionaryEditor(picker, label, kind, fieldName);
        row.Children.Add(picker);
        Grid.SetColumn(add, 1);
        row.Children.Add(add);
        return row;
    }

    private void ShowDictionaryEditor(Button anchor, TextBlock label, AnnotationDictionaryKind kind, string fieldName)
    {
        if (dictionary is null || annotationWriting || dictionaryAdding) return;
        var input = new TextBox { MaxLength = AnnotationStore.NameLimit, Margin = new Thickness(0, 6, 0, 0), Style = ThemeStyle("RoundedTextInput") };
        System.Windows.Automation.AutomationProperties.SetName(input, fieldName + "名称");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var save = new Button { Content = "保存", MinWidth = 64, Style = ThemeStyle("PrimaryButton") };
        save.SetResourceReference(Control.ForegroundProperty, "CanvasBrush");
        var cancel = new Button { Content = "取消", MinWidth = 64, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { cancel, save }
        };
        var editor = new StackPanel { Margin = new Thickness(8), Children = { FieldLabel("新增" + fieldName, 0), input, error, buttons } };
        var popup = CreateAnnotationPopup(anchor, editor);
        cancel.Click += (_, _) => popup.IsOpen = false;
        popup.Opened += (_, _) => input.Focus();
        editor.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SaveAsync(); }
            else if (e.Key == Key.Escape && !dictionaryAdding) { e.Handled = true; popup.IsOpen = false; }
        };
        save.Click += async (_, _) => await SaveAsync();
        popup.IsOpen = true;

        async Task SaveAsync()
        {
            if (dictionaryAdding || annotationWriting) return;
            string name = input.Text.Trim();
            error.Visibility = Visibility.Collapsed;
            if (name.Length == 0)
            {
                error.Text = "请输入名称";
                error.Visibility = Visibility.Visible;
                input.Focus();
                return;
            }

            string imagePath = CurrentPath;
            dictionaryAdding = true;
            editor.IsEnabled = false;
            try
            {
                dictionary = await client.AddAnnotationDictionaryItemAsync(kind, name, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                var items = kind == AnnotationDictionaryKind.Category ? dictionary.Categories : dictionary.Plans;
                var item = items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (item is not null && CurrentPath == imagePath) SetPickerValue(anchor, label, item.Name);
                annotationHint!.Visibility = Visibility.Collapsed;
                popup.IsOpen = false;
                Toast.Show(this, "字典已保存");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存标注字典失败；分组 {Kind}", kind);
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                if (!popup.IsOpen && !lifetime.IsCancellationRequested) Toast.Show(this, "字典保存失败：" + ex.Message, ToastKind.Danger);
            }
            finally
            {
                dictionaryAdding = false;
                editor.IsEnabled = true;
            }
        }
    }

    private void SetPickerValue(Button picker, TextBlock label, string name)
    {
        label.Text = name;
        if (ReferenceEquals(picker, categoryPicker)) categoryCode = name;
        else planCode = name;
        MarkDirty();
    }

    // 自绘下拉：弹出层里放一列按钮，避开 ContextMenu/MenuItem 原生模板的勾选列白条。
    private void ShowPickerMenu(Button button, TextBlock label, string placeholder)
    {
        var items = ReferenceEquals(button, categoryPicker) ? dictionary?.Categories : dictionary?.Plans;
        if (items is null || items.Length == 0)
        {
            annotationHint!.Text = "服务端未配置标注字典，暂时无法选择。";
            annotationHint.Visibility = Visibility.Visible;
            return;
        }
        var stack = new StackPanel();
        var ghost = ThemeStyle("GhostButton");
        foreach (var item in items)
        {
            var option = new Button
            {
                Content = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(2, 1, 2, 1),
                MinHeight = 28
            };
            if (ghost is not null) option.Style = ghost;
            option.Click += (_, _) =>
            {
                SetPickerValue(button, label, item.Name);
                if (pickerPopup is not null) pickerPopup.IsOpen = false;
            };
            stack.Children.Add(option);
        }
        var scroll = new ScrollViewer
        {
            Content = stack,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        CreateAnnotationPopup(button, scroll).IsOpen = true;
    }

    private Popup CreateAnnotationPopup(Button anchor, UIElement content)
    {
        if (pickerPopup is not null) pickerPopup.IsOpen = false;
        var border = new Border
        {
            Background = ThemeBrush("SurfaceBrush", Color.FromRgb(0x24, 0x28, 0x2D)),
            BorderBrush = ThemeBrush("LineBrush", Color.FromRgb(0x36, 0x3C, 0x43)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Child = content
        };
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = border,
            Width = Math.Max(anchor.ActualWidth, 240)
        };
        pickerPopup = popup;
        return popup;
    }

    private void MarkDirty()
    {
        if (loadingAnnotation || annotation is null) return;
        annotationDirty = true;
        if (annotationError is not null) annotationError.Visibility = Visibility.Collapsed;
    }

    private async Task LoadAnnotationDictionaryAsync()
    {
        if (annotation is null) return;
        try { dictionary = await client.GetAnnotationDictionaryAsync(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取标注字典失败");
            dictionary = new RemoteDictionary([], []);
        }
        if (dictionary.Categories.Length == 0 && dictionary.Plans.Length == 0)
        {
            annotationHint!.Text = "服务端未配置标注字典（RpaDataItems / RpaDataItemDetails），下拉暂时为空。";
            annotationHint.Visibility = Visibility.Visible;
        }
    }

    private Task LoadAnnotationForCurrentAsync()
    {
        if (annotation is null || categoryLabel is null) return Task.CompletedTask;
        loadingAnnotation = true;
        try
        {
            categoryCode = planCode = "";
            categoryLabel.Text = "请选择异常归类";
            planLabel!.Text = "请选择异常处理方案";
            noteBox!.Clear();
            operatorBox!.Text = operatorName;
            UpdateCurrentAnnotationStatus();
            annotationError!.Visibility = Visibility.Collapsed;
        }
        finally
        {
            loadingAnnotation = false;
            annotationDirty = false;
        }
        return Task.CompletedTask;
    }

    private void UpdateCurrentAnnotationStatus()
    {
        string imageName = images[index].Name;
        var history = annotation!.GetImageHistory(imageName);
        var latest = history.FirstOrDefault();
        bool annotated = latest is not null;
        annotationImageName!.Text = imageName;
        annotationStatus!.Text = annotated ? $"已标注 {history.Count} 次" : "未标注";
        annotationStatus.SetResourceReference(TextBlock.ForegroundProperty, annotated ? "SuccessBrush" : "MutedBrush");
        annotationStatusBadge!.SetResourceReference(Border.BackgroundProperty, annotated ? "AccentSoftBrush" : "SurfaceBrush");
        annotationSummary!.Text = latest is null ? "" : $"最近一次 · {latest.CategoryName} / {latest.PlanName}";
        annotationSummary.Visibility = annotated ? Visibility.Visible : Visibility.Collapsed;
        submitButton!.Content = annotated ? "新增标注" : "提交标注";
        revokeButton!.Visibility = annotated ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SubmitAnnotationAsync()
    {
        if (annotation is null || annotationWriting || dictionaryAdding) return;
        annotationError!.Visibility = Visibility.Collapsed;
        string operatorText = (operatorBox?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(categoryCode)) { ShowAnnotationError("请选择异常归类"); return; }
        if (string.IsNullOrEmpty(planCode)) { ShowAnnotationError("请选择异常处理方案"); return; }
        if (operatorText.Length == 0) { ShowAnnotationError("请填写操作人"); return; }
        string imageName = images[index].Name;
        await RunAnnotationWriteAsync("正在提交…", "提交失败", "标注已提交", async () =>
        {
            await client.SaveAnnotationAsync(annotation.FolderPath, categoryCode, planCode,
                noteBox?.Text, operatorText, imageName);
            SaveOperatorName(operatorText);
        });
    }

    private async Task RevokeAnnotationAsync()
    {
        if (annotation is null || annotationWriting || dictionaryAdding) return;
        string imageName = images[index].Name;
        if (annotation.GetImageHistory(imageName).Count == 0) return;
        await RunAnnotationWriteAsync("正在撤销…", "撤销失败", "该图片的标注已撤销",
            () => client.DeleteImageAnnotationsAsync(annotation.FolderPath, imageName));
    }

    private async Task RunAnnotationWriteAsync(string progress, string failure, string success, Func<Task> write)
    {
        string path = CurrentPath;
        annotationWriting = true;
        submitButton!.IsEnabled = false;
        revokeButton!.IsEnabled = false;
        annotationError!.Visibility = Visibility.Collapsed;
        annotationStatus!.Text = progress;
        try
        {
            await write();
            annotationDirty = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Operation}；图片 {Path}", failure, path);
            ShowAnnotationError(ex.Message);
            UpdateCurrentAnnotationStatus();
            Toast.Show(this, failure + "：" + ex.Message, ToastKind.Danger, 3200);
            return;
        }
        finally
        {
            annotationWriting = false;
            submitButton.IsEnabled = true;
            revokeButton.IsEnabled = true;
        }
        if (lifetime.IsCancellationRequested) return;
        var owner = Owner;
        Close();
        Toast.Show(owner, success, ToastKind.Success);
    }

    private void ShowAnnotationError(string message)
    {
        annotationError!.Text = message;
        annotationError.Visibility = Visibility.Visible;
    }

    private static string LoadOperatorName()
    {
        try
        {
            string path = AppLogging.GetSettingsPath("operator.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<OperatorSettings>(File.ReadAllText(path)) is { } saved)
                return saved.Name;
        }
        catch (Exception ex) { Log.Debug(ex, "读取操作人失败"); }
        return Environment.UserName;
    }

    private static void SaveOperatorName(string name)
    {
        try
        {
            string path = AppLogging.GetSettingsPath("operator.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new OperatorSettings(name)), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Debug(ex, "保存操作人失败"); }
    }

    private sealed record OperatorSettings(string Name);

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
            annotationList.SelectedIndex = index;
            if (index >= 0 && index < thumbs.Count) annotationList.ScrollIntoView(thumbs[index]);
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

    private Button CreateTitleButton(string geometryKey, string tooltip)
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
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC))
        };
        var style = ThemeStyle("GhostButton");
        if (style is not null) button.Style = style;
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
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
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
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A))
    };

    private System.Windows.Shapes.Path CreateInfoIcon(string geometryKey) => new()
    {
        Data = (Geometry)FindResource(geometryKey),
        Width = 14,
        Height = 14,
        Stretch = Stretch.None,
        Fill = Brushes.Transparent,
        Stroke = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC)),
        StrokeThickness = 1.4,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
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
        var point = e.GetPosition(stage);
        double zone = Math.Clamp(stage.ActualWidth * 0.16, 96, 220);
        RevealArrow(previousArrow, point.X <= zone);
        RevealArrow(nextArrow, point.X >= stage.ActualWidth - zone);
    }

    private void Stage_MouseEnter(object sender, MouseEventArgs e)
    {
        arrowHideTimer.Stop();
        SetArrowVisual(previousArrow, previousArrow.IsEnabled ? ArrowDimOpacity : 0);
        SetArrowVisual(nextArrow, nextArrow.IsEnabled ? ArrowDimOpacity : 0);
    }

    private void Stage_MouseLeave(object sender, MouseEventArgs e)
    {
        arrowHideTimer.Stop();
        arrowHideTimer.Start();
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
        double fit = Math.Min(canvas.ActualWidth / bitmap.PixelWidth, canvas.ActualHeight / bitmap.PixelHeight);
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

    // 切图前确认未提交的标注，避免直接丢掉用户填的内容。
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (annotation is null || !annotationDirty) return true;
        if (!Dialog.Confirm(this, "当前图片的标注尚未提交，切换后填写的内容会丢失。", "未提交的标注")) return false;
        annotationDirty = false;
        return true;
    }

    public async Task MoveAsync(int direction)
    {
        if (index + direction < 0 || index + direction >= images.Length) return;
        if (!await ConfirmDiscardAsync()) { SyncAnnotationSelection(); return; }
        index += direction; await ShowCurrentAsync();
    }

    public async Task MoveToAsync(int target)
    {
        if (target < 0 || target >= images.Length) return;
        if (target == index) { SyncThumbSelection(); return; }
        if (!await ConfirmDiscardAsync()) { SyncAnnotationSelection(); SyncThumbSelection(); return; }
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
            if (version == loadVersion)
            {
                previousArrow.IsEnabled = index > 0;
                nextArrow.IsEnabled = index < images.Length - 1;
                if (!previousArrow.IsEnabled) SetArrowVisual(previousArrow, 0, 0);
                if (!nextArrow.IsEnabled) SetArrowVisual(nextArrow, 0, 0);
            }
        }
    }

    private void UpdateInfo(BitmapSource bitmap)
    {
        formatText.Text = Path.GetExtension(CurrentPath).TrimStart('.').ToUpperInvariant();
        dimensionText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        sizeText.Text = images[index].Length > 0 ? FormatSize(images[index].Length) : "";
        infoPanel.ToolTip = $"{images[index].Name}\n{CurrentPath}\n创建：{images[index].CreatedLocal:yyyy.MM.dd HH:mm}";
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
