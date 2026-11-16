using System.IO;
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

    public ImageViewer(RemoteFileClient client, RemoteEntry[] images, string selected)
    {
        UseDarkTheme();
        this.client = client;
        this.images = images.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        index = Array.FindIndex(this.images, e => e.Path == selected);
        if (index < 0) throw new IOException("图片已从目录移除，请刷新后重试");
        for (int i = 0; i < this.images.Length; i++) thumbs.Add(new(i, this.images[i], i + 1));

        Width = 1180; Height = 820; MinWidth = 720; MinHeight = 480;
        Title = "图片查看";
        Background = ThemeBrush("CanvasBrush", Color.FromRgb(0x17, 0x1A, 0x1D));
        Foreground = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
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
        var stageBrush = header;
        var stageText = new SolidColorBrush(Color.FromRgb(0xAE, 0xB8, 0xC2));

        var infoBrush = ThemeBrush("InkBrush", Color.FromRgb(0xE7, 0xE9, 0xEC));
        formatText.Foreground = infoBrush;
        dimensionText.Foreground = infoBrush;
        sizeText.Foreground = infoBrush;
        titleText.Foreground = Foreground;
        stageMessage.Foreground = stageText;
        previousArrow = CreateArrow(false, "上一张图片", HorizontalAlignment.Left);
        nextArrow = CreateArrow(true, "下一张图片", HorizontalAlignment.Right);

        var panel = new DockPanel();

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
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumnSpan(titleText, 2);
        titleGrid.Children.Add(titleText);
        Grid.SetColumn(titleButtons, 1);
        titleGrid.Children.Add(titleButtons);
        titleBar = new Border
        {
            Height = 44,
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0),
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
        stage.Background = stageBrush;
        stage.ClipToBounds = true;
        stage.Children.Add(canvas);
        stage.Children.Add(stageMessage);
        stage.Children.Add(previousArrow);
        stage.Children.Add(nextArrow);
        canvas.SizeChanged += (_, _) => { if (fitMode) FitToWindow(); else UpdateCursor(); };
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
            Height = 36,
            Padding = new Thickness(8, 0, 8, 0),
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0),
            Child = statusGrid
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        panel.Children.Add(statusBar);
        // 先停靠状态栏，再停靠缩略图条，最后让舞台填充剩余空间。
        panel.Children.Add(thumbBar);
        panel.Children.Add(stage);

        Content = panel;

        Loaded += async (_, _) => { FitToWindow(); await ShowCurrentAsync(); };
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
        Closed += (_, _) => { lifetime.Cancel(); loadCancel?.Cancel(); thumbScrollTimer.Stop(); };
        UpdateZoomUi();
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

    private Button CreateArrow(bool right, string tooltip, HorizontalAlignment alignment)
    {
        // 用同色描边 + 圆角连接，把三角的三个尖角磨圆。
        var triangle = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(right ? "M0,0 L8,6.5 L0,13 Z" : "M8,0 L0,6.5 L8,13 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xE8)),
            Stroke = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xE8)),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 8,
            Height = 12,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        var pill = new Border
        {
            Width = 20,
            Height = 54,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x42)),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(right ? 0 : 8, 0, right ? 8 : 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = triangle
        };
        // 可见按钮很小，但点击热区覆盖整条边缘，和 Windows 照片一致。
        var button = new Button
        {
            Content = pill,
            ToolTip = tooltip,
            Width = 96,
            Height = 160,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
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

        var scale = (ScaleTransform)pill.RenderTransform;
        void Press(bool down)
        {
            var duration = TimeSpan.FromMilliseconds(down ? 70 : 140);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(down ? 0.9 : 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(down ? 0.9 : 1, duration) { EasingFunction = ease });
        }
        button.MouseEnter += (_, _) => { if (button.IsEnabled) button.Opacity = 1; };
        button.MouseLeave += (_, _) => { button.Opacity = 0; Press(false); };
        button.PreviewMouseLeftButtonDown += (_, _) => Press(true);
        button.PreviewMouseLeftButtonUp += (_, _) => Press(false);
        return button;
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

    public async Task MoveAsync(int direction)
    {
        if (index + direction < 0 || index + direction >= images.Length) return;
        index += direction; await ShowCurrentAsync();
    }

    public async Task MoveToAsync(int target)
    {
        if (target < 0 || target >= images.Length) return;
        if (target == index) { SyncThumbSelection(); return; }
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
                if (!previousArrow.IsEnabled) previousArrow.Opacity = 0;
                if (!nextArrow.IsEnabled) nextArrow.Opacity = 0;
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
    public int Index { get; } = index;
    public string Path { get; } = entry.Path;
    public int Number { get; } = number;
    public string Tip { get; } = $"{number}. {entry.Name}\r\n{entry.CreatedLocal:yyyy.MM.dd HH:mm}\r\n{SizeText(entry.Length)}";
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
