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
    private readonly TextBlock status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock imageName = new() { FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
    private readonly TextBlock titleText = new() { FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button previous = new() { Content = "← 上一张", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button next = new() { Content = "下一张 →", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button previousArrow = CreateArrow("‹", "上一张图片", HorizontalAlignment.Left);
    private readonly Button nextArrow = CreateArrow("›", "下一张图片", HorizontalAlignment.Right);
    private readonly CancellationTokenSource lifetime = new();
    private int index;
    private bool busy;
    public string CurrentPath => images[index].Path;
    public ImageViewer(RemoteFileClient client, RemoteEntry[] images, string selected)
    {
        this.client = client;
        this.images = images.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        index = Array.FindIndex(this.images, e => e.Path == selected);
        if (index < 0) throw new IOException("图片已从目录移除，请刷新后重试");
        Width = 1100; Height = 800; Background = Brushes.WhiteSmoke;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, ResizeBorderThickness = new Thickness(6), UseAeroCaptionButtons = false });
        var panel = new DockPanel();
        var titleBar = new DockPanel { Height = 32, Background = Brushes.WhiteSmoke };
        var titleButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var minimize = new Button { Content = "—", Width = 46, Height = 32, ToolTip = "最小化" };
        var maximize = new Button { Content = "□", Width = 46, Height = 32, ToolTip = "最大化 / 还原" };
        var close = new Button { Content = "×", Width = 46, Height = 32, ToolTip = "关闭" };
        foreach (var button in new[] { minimize, maximize, close }) { WindowChrome.SetIsHitTestVisibleInChrome(button, true); titleButtons.Children.Add(button); }
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        maximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(titleButtons, Dock.Right); titleBar.Children.Add(titleButtons); titleBar.Children.Add(titleText);
        DockPanel.SetDock(titleBar, Dock.Top); panel.Children.Add(titleBar);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(previous); controls.Children.Add(next);
        controls.Children.Add(imageName);
        controls.Children.Add(new TextBlock { Text = "按创建时间从早到晚；Left / Right 切换", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) });
        DockPanel.SetDock(controls, Dock.Top); panel.Children.Add(controls);
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        var preview = new Grid(); preview.Children.Add(image); preview.Children.Add(previousArrow); preview.Children.Add(nextArrow);
        panel.Children.Add(preview); Content = panel;
        Loaded += async (_, _) => await ShowCurrentAsync();
        previous.Click += async (_, _) => await MoveAsync(-1); next.Click += async (_, _) => await MoveAsync(1);
        previousArrow.Click += async (_, _) => await MoveAsync(-1); nextArrow.Click += async (_, _) => await MoveAsync(1);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) { e.Handled = true; await MoveAsync(e.Key == Key.Left ? -1 : 1); }
        };
        Closed += (_, _) => lifetime.Cancel();
    }
    private static Button CreateArrow(string glyph, string tooltip, HorizontalAlignment alignment) => new()
    {
        Content = glyph, ToolTip = tooltip, HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Center,
        Width = 42, Height = 64, Margin = new Thickness(12, 0, 12, 0), FontSize = 32,
        Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(190, 40, 40, 40)),
        BorderThickness = new Thickness(0), Cursor = Cursors.Hand
    };
    public async Task MoveAsync(int direction)
    {
        if (busy || index + direction < 0 || index + direction >= images.Length) return;
        index += direction; await ShowCurrentAsync();
    }
    public async Task ShowCurrentAsync()
    {
        if (busy) return;
        busy = true; previous.IsEnabled = next.IsEnabled = previousArrow.IsEnabled = nextArrow.IsEnabled = false;
        image.Source = null;
        Title = $"图片 · {index + 1}/{images.Length} · {CurrentPath}"; titleText.Text = Title; imageName.Text = images[index].Name; status.Text = "正在读取图片……";
        try
        {
            byte[] bytes = await client.ReadImageAsync(CurrentPath, lifetime.Token);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 2400; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap;
            status.Inlines.Clear();
            status.Inlines.Add(new Run($"{index + 1}/{images.Length}  ") { FontWeight = FontWeights.Bold });
            status.Inlines.Add(new Run(CurrentPath[..(CurrentPath.Length - images[index].Name.Length)]) { FontWeight = FontWeights.Bold });
            status.Inlines.Add(new Run(images[index].Name) { FontWeight = FontWeights.Bold });
            status.Inlines.Add(new Run($"  创建：{images[index].CreatedLocal:yyyy-MM-dd HH:mm:ss}"));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error(ex, "图片显示失败；路径 {RemotePath}", CurrentPath); status.Text = "图片读取失败：" + ex.Message; }
        finally { busy = false; previous.IsEnabled = previousArrow.IsEnabled = index > 0; next.IsEnabled = nextArrow.IsEnabled = index < images.Length - 1; }
    }
}
