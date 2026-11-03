using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly TextBlock imageName = new()
    {
        FontWeight = FontWeights.Bold,
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
    private readonly Button previous = new() { Content = "← 上一张", Margin = new Thickness(0, 0, 4, 0) };
    private readonly Button next = new() { Content = "下一张 →" };
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
    private int index;
    private bool busy;
    public string CurrentPath => images[index].Path;
    public ImageViewer(RemoteFileClient client, RemoteEntry[] images, string selected)
    {
        ThemeLoader.Apply(this);
        this.client = client;
        this.images = images.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        index = Array.FindIndex(this.images, e => e.Path == selected);
        if (index < 0) throw new IOException("图片已从目录移除，请刷新后重试");

        Width = 1100; Height = 800; MinWidth = 720; MinHeight = 480;
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
        imageName.Foreground = Foreground;
        stageMessage.Foreground = stageText;
        previousArrow = CreateArrow(false, "上一张图片", HorizontalAlignment.Left);
        nextArrow = CreateArrow(true, "下一张图片", HorizontalAlignment.Right);
        var secondary = ThemeStyle("SecondaryButton");
        if (secondary is not null) { previous.Style = secondary; next.Style = secondary; }

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

        // 工具栏
        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        nav.Children.Add(previous);
        nav.Children.Add(next);
        var controls = new DockPanel { LastChildFill = true, Height = 40 };
        DockPanel.SetDock(nav, Dock.Left); controls.Children.Add(nav);
        controls.Children.Add(imageName);
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

        // 底部状态栏
        var statusBar = new Border
        {
            Height = 28,
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = status
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        panel.Children.Add(statusBar);

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
        previous.Click += async (_, _) => await MoveAsync(-1);
        next.Click += async (_, _) => await MoveAsync(1);
        previousArrow.Click += async (_, _) => await MoveAsync(-1);
        nextArrow.Click += async (_, _) => await MoveAsync(1);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) { e.Handled = true; await MoveAsync(e.Key == Key.Left ? -1 : 1); }
        };
        Closed += (_, _) => lifetime.Cancel();
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
        if (busy || index + direction < 0 || index + direction >= images.Length) return;
        index += direction; await ShowCurrentAsync();
    }
    public async Task ShowCurrentAsync()
    {
        if (busy) return;
        busy = true;
        previous.IsEnabled = next.IsEnabled = previousArrow.IsEnabled = nextArrow.IsEnabled = false;
        image.Source = null;
        Title = $"图片 · {index + 1}/{images.Length} · {CurrentPath}";
        titleText.Text = Title;
        imageName.Text = images[index].Name;
        status.Text = "正在读取图片……";
        stageMessage.Text = "正在读取图片…";
        stageMessage.Visibility = Visibility.Visible;
        try
        {
            byte[] bytes = await client.ReadImageAsync(CurrentPath, lifetime.Token);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 2400;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
            stageMessage.Visibility = Visibility.Collapsed;
            status.Inlines.Clear();
            status.Inlines.Add(new Run($"{index + 1}/{images.Length}  ") { FontWeight = FontWeights.Bold });
            status.Inlines.Add(new Run(CurrentPath[..(CurrentPath.Length - images[index].Name.Length)]) { FontWeight = FontWeights.Bold });
            status.Inlines.Add(new Run(images[index].Name) { FontWeight = FontWeights.Bold });
            status.Inlines.Add(new Run($"  创建：{images[index].CreatedLocal:yyyy.MM.dd HH:mm}"));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex, "图片显示失败；路径 {RemotePath}", CurrentPath);
            status.Text = "图片读取失败：" + ex.Message;
            stageMessage.Text = "图片读取失败";
            stageMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            busy = false;
            previous.IsEnabled = previousArrow.IsEnabled = index > 0;
            next.IsEnabled = nextArrow.IsEnabled = index < images.Length - 1;
        }
    }
    private Brush ThemeBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private Style? ThemeStyle(string key) => TryFindResource(key) as Style;
}
