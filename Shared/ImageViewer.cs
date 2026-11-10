using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;
public sealed class ImageViewer : Window
{
    private readonly RemoteFileClient client;
    private readonly RemoteEntry[] images;
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new()
    {
        Margin = new Thickness(10, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock pathText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 12, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock titleText = new()
    {
        FontWeight = FontWeights.Bold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 4, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Button previousArrow;
    private readonly Button nextArrow;
    private readonly TextBlock stageMessage = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
        Margin = new Thickness(24),
        Visibility = Visibility.Collapsed
    };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, BitmapSource> imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> cacheOrder = new();
    private const long CacheLimitBytes = 200L * 1024 * 1024;
    private readonly ObservableCollection<ImageThumbItem> thumbs = [];
    private readonly ListBox thumbList = new();
    private readonly TextBox pageBox = new();
    private readonly TextBlock pageTotal = new();
    private readonly Dictionary<string, BitmapSource> thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> thumbLoading = [];
    private readonly SemaphoreSlim thumbSlots = new(2);
    private bool syncingThumb;
    private Task<BitmapSource?>? prefetchTask;
    private string? prefetchPath;
    private CancellationTokenSource? loadCancel;
    private long cacheBytes;
    private int loadVersion;
    private int index;
    public string CurrentPath => images[index].Path;
    public ImageViewer(RemoteFileClient client, RemoteEntry[] images, string selected)
    {
        ThemeLoader.Apply(this);
        this.client = client;
        this.images = images.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        index = Array.FindIndex(this.images, e => e.Path == selected);
        if (index < 0) throw new IOException("图片已从目录移除，请刷新后重试");
        for (int i = 0; i < this.images.Length; i++) thumbs.Add(new(i, this.images[i], i + 1));

        Width = 1100; Height = 800; MinWidth = 720; MinHeight = 480;
        Title = "图片查看";
        titleText.Text = "图片查看";
        Background = ThemeBrush("CanvasBrush", Color.FromRgb(0xF3, 0xF3, 0xF3));
        Foreground = ThemeBrush("InkBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false
        });

        var line = ThemeBrush("LineBrush", Color.FromRgb(0xD6, 0xD6, 0xD6));
        var header = ThemeBrush("HeaderBrush", Color.FromRgb(0xF7, 0xF7, 0xF7));
        var surface = ThemeBrush("SurfaceBrush", Colors.White);
        var muted = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61));
        var danger = ThemeBrush("DangerBrush", Color.FromRgb(0xB4, 0x23, 0x18));
        var stageBrush = new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x1D));
        var stageText = new SolidColorBrush(Color.FromRgb(0xAE, 0xB8, 0xC2));

        status.Foreground = muted;
        pathText.Foreground = Foreground;
        stageMessage.Foreground = stageText;
        previousArrow = CreateArrow(false, "上一张图片", HorizontalAlignment.Left);
        nextArrow = CreateArrow(true, "下一张图片", HorizontalAlignment.Right);

        var panel = new DockPanel();

        // 标题栏
        var titleButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var minimize = CreateTitleButton("—", "最小化", 14);
        var maximize = CreateTitleButton("□", "最大化 / 还原", 14);
        var close = CreateTitleButton("×", "关闭", 16);
        close.Foreground = danger;
        foreach (var button in new[] { minimize, maximize, close })
        {
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            titleButtons.Children.Add(button);
        }
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        maximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        close.Click += (_, _) => Close();
        var titleDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleButtons, Dock.Right);
        titleDock.Children.Add(titleButtons);
        titleDock.Children.Add(titleText);
        var titleBar = new Border
        {
            Height = 32,
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleDock
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        panel.Children.Add(titleBar);

        // 工具栏：只显示一次完整路径和文件名
        var controls = new DockPanel { LastChildFill = true, Height = 40 };
        controls.Children.Add(pathText);
        var controlsBar = new Border
        {
            Height = 40,
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = controls
        };
        DockPanel.SetDock(controlsBar, Dock.Top);
        panel.Children.Add(controlsBar);

        // 底部状态栏（左侧显示信息，右侧页码跳转）
        pageBox.Width = 44; pageBox.Height = 22; pageBox.MinHeight = 20; pageBox.FontSize = 11;
        pageBox.TextAlignment = TextAlignment.Center;
        pageBox.VerticalContentAlignment = VerticalAlignment.Center;
        pageBox.Padding = new Thickness(0);
        pageBox.ToolTip = "输入页码后按回车跳转";
        pageBox.KeyDown += async (_, e) => { if (e.Key != Key.Enter) return; e.Handled = true; await JumpToPageAsync(); };
        pageBox.LostFocus += (_, _) => pageBox.Text = (index + 1).ToString();
        pageTotal.VerticalAlignment = VerticalAlignment.Center;
        pageTotal.FontSize = 11;
        pageTotal.Foreground = muted;
        pageTotal.Text = $" / {images.Length}";
        var pagePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        pagePanel.Children.Add(new TextBlock { Text = "第", FontSize = 11, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        pagePanel.Children.Add(pageBox);
        pagePanel.Children.Add(pageTotal);
        var locate = new Button
        {
            Content = "定位",
            Height = 22,
            MinHeight = 20,
            Padding = new Thickness(6, 0, 6, 0),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "滚动到当前图片"
        };
        locate.Click += (_, _) => { SyncThumbSelection(); EnsureThumbsAround(index); };
        pagePanel.Children.Add(locate);
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.Children.Add(status);
        Grid.SetColumn(pagePanel, 1); statusGrid.Children.Add(pagePanel);
        var statusBar = new Border
        {
            Height = 28,
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = statusGrid
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        panel.Children.Add(statusBar);

        // 缩略图条（位于状态栏上方，按可视范围懒加载）
        thumbList.ItemsSource = thumbs;
        thumbList.Height = 82;
        thumbList.Padding = new Thickness(6, 2, 6, 2);
        thumbList.Background = Brushes.Transparent;
        thumbList.BorderThickness = new Thickness(0);
        thumbList.SelectionChanged += Thumb_SelectionChanged;
        thumbList.PreviewMouseWheel += Thumb_MouseWheel;
        thumbList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Thumb_ScrollChanged));
        thumbList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (thumbList.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
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
        var thumbBar = new Border
        {
            Height = 86,
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = thumbList
        };
        DockPanel.SetDock(thumbBar, Dock.Bottom);
        panel.Children.Add(thumbBar);

        // 深色图片画布
        var preview = new Grid();
        preview.Children.Add(image);
        preview.Children.Add(stageMessage);
        preview.Children.Add(previousArrow);
        preview.Children.Add(nextArrow);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var stage = new Border { Background = stageBrush, ClipToBounds = true, Child = preview };
        panel.Children.Add(stage);
        Content = panel;

        Loaded += async (_, _) => await ShowCurrentAsync();
        previousArrow.Click += async (_, _) => await MoveAsync(-1);
        nextArrow.Click += async (_, _) => await MoveAsync(1);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) { e.Handled = true; await MoveAsync(e.Key == Key.Left ? -1 : 1); }
        };
        Closed += (_, _) => { lifetime.Cancel(); loadCancel?.Cancel(); };
    }
    private Button CreateTitleButton(string glyph, string tooltip, double fontSize)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = tooltip,
            Width = 46,
            Height = 32,
            FontSize = fontSize,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        var style = ThemeStyle("GhostButton");
        if (style is not null) button.Style = style;
        return button;
    }
    private Button CreateArrow(bool right, string tooltip, HorizontalAlignment alignment)
    {
        var triangle = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(right ? "M0,0 L10,8 L0,16 Z" : "M10,0 L0,8 L10,16 Z"),
            Fill = Brushes.White,
            Width = 10,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        var button = new Button
        {
            Content = triangle,
            ToolTip = tooltip,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 38,
            Height = 56,
            Margin = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x25, 0x2A)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        var style = ThemeStyle("ImageArrowButton");
        if (style is not null) button.Style = style;
        return button;
    }
    public async Task MoveAsync(int direction)
    {
        if (index + direction < 0 || index + direction >= images.Length) return;
        index += direction; await ShowCurrentAsync();
    }
    public async Task MoveToAsync(int target)
    {
        if (target < 0 || target >= images.Length) return;
        if (target == index)
        {
            SyncThumbSelection();
            return;
        }
        index = target;
        await ShowCurrentAsync();
    }
    private async Task JumpToPageAsync()
    {
        if (!int.TryParse(pageBox.Text.Trim(), out int number) || number < 1 || number > images.Length)
        {
            pageBox.Text = (index + 1).ToString();
            return;
        }
        await MoveToAsync(number - 1);
    }
    private void SyncThumbSelection()
    {
        syncingThumb = true;
        try
        {
            thumbList.SelectedIndex = index;
            if (index >= 0 && index < thumbs.Count) thumbList.ScrollIntoView(thumbs[index]);
            pageBox.Text = (index + 1).ToString();
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
        if (FindVisualChild<ScrollViewer>(thumbList) is not { } scroll) return;
        // 一格滚轮约滚动 3 张缩略图。
        double step = e.Delta / 120d * 300d;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - step);
        e.Handled = true;
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
    // 只加载真正生成的缩略图容器，避免用固定宽度估算滚动区间产生累计误差。
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
        bitmap.DecodePixelWidth = 128;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    private static BitmapSource CreateThumb(BitmapSource source)
    {
        double scale = Math.Min(1d, 128d / Math.Max(1, source.PixelWidth));
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }
    private static DataTemplate BuildThumbTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.WidthProperty, 96.0);
        grid.SetValue(FrameworkElement.HeightProperty, 64.0);
        grid.SetValue(Panel.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x1D)));
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
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImageThumbItem.Number)));
        label.SetValue(TextBlock.FontSizeProperty, 10.0);
        label.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        label.SetValue(TextBlock.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 2, 2));
        label.SetValue(TextBlock.PaddingProperty, new Thickness(3, 0, 3, 1));
        grid.AppendChild(label);
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
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.PaddingProperty, new Thickness(2));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, ThemeBrush("AccentBrush", Color.FromRgb(0x0F, 0x6C, 0xBD)), "Bd"));
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x22, 0x0F, 0x6C, 0xBD)), "Bd"));
        template.Triggers.Add(selected);
        var hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x11, 0, 0, 0)), "Bd"));
        template.Triggers.Add(hover);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(2, 0, 0, 0)));
        style.Setters.Add(new Setter(Control.VerticalAlignmentProperty, VerticalAlignment.Center));
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
        string name = images[index].Name;
        string directory = CurrentPath[..(CurrentPath.Length - name.Length)];
        pathText.Inlines.Clear();
        pathText.Inlines.Add(new Run($"{index + 1}/{images.Length}  ") { FontWeight = FontWeights.Bold });
        pathText.Inlines.Add(new Run(directory));
        pathText.Inlines.Add(new Run(name) { FontWeight = FontWeights.Bold });
        pathText.ToolTip = CurrentPath;
        // 保留当前图片，避免加载下一张时出现黑屏；只有首次加载才显示占位文字。
        if (image.Source is null)
        {
            status.Text = "正在读取图片……";
            stageMessage.Text = "正在读取图片…";
            stageMessage.Visibility = Visibility.Visible;
        }
        else status.Text = "正在加载下一张图片……";
        try
        {
            var bitmap = await LoadImageAsync(CurrentPath, token);
            if (version != loadVersion) return;
            image.Source = bitmap;
            stageMessage.Visibility = Visibility.Collapsed;
            status.Inlines.Clear();
            status.Inlines.Add(new Run($"创建：{images[index].CreatedLocal:yyyy.MM.dd HH:mm}"));
            status.Inlines.Add(new Run($"   ·   {bitmap.PixelWidth} × {bitmap.PixelHeight}"));
            if (images[index].Length > 0) status.Inlines.Add(new Run($"   ·   {FormatSize(images[index].Length)}"));
            StartPrefetch();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || lifetime.IsCancellationRequested || version != loadVersion) { }
        catch (Exception ex)
        {
            Log.Error(ex, "图片显示失败；路径 {RemotePath}", CurrentPath);
            status.Text = "图片读取失败：" + ex.Message;
            stageMessage.Text = "图片读取失败";
            stageMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            if (version == loadVersion)
            {
                previousArrow.IsEnabled = index > 0;
                nextArrow.IsEnabled = index < images.Length - 1;
            }
        }
    }
    private static BitmapSource Decode(byte[] bytes)    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 2400;
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
