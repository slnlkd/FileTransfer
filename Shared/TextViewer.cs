using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;
public sealed class TextViewer : Window
{
    public TextBox Editor { get; } = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock status = new()
    {
        Padding = new Thickness(10, 4, 10, 4),
        FontSize = 11,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Button save = new()
    {
        Content = "保存到服务端（Ctrl+S）",
        Margin = new Thickness(8, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly RemoteFileClient client;
    private readonly string path;
    private string? version;
    private readonly string? encoding;
    private bool dirty;
    private bool saving;
    public TextViewer(RemoteFileClient client, string path, RemoteResponse response)
    {
        ThemeLoader.Apply(this);
        this.client = client; this.path = path; version = response.Version; encoding = response.EncodingName;
        Title = "文本 · " + path;
        Width = 1000; Height = 720; MinWidth = 700; MinHeight = 480;
        Background = ThemeBrush("CanvasBrush", Color.FromRgb(0xF3, 0xF3, 0xF3));
        Foreground = ThemeBrush("InkBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var line = ThemeBrush("LineBrush", Color.FromRgb(0xD6, 0xD6, 0xD6));
        var surface = ThemeBrush("SurfaceBrush", Colors.White);
        var header = ThemeBrush("HeaderBrush", Color.FromRgb(0xF7, 0xF7, 0xF7));
        var muted = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61));
        var iconBrush = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61));

        status.Foreground = muted;
        status.Background = header;
        var saveStyle = ThemeStyle("PrimaryButton");
        if (saveStyle is not null) save.Style = saveStyle;

        var panel = new DockPanel();

        // 顶部文件信息
        var headerPanel = new DockPanel { Height = 40, LastChildFill = true };
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,1 H9 L14,6 V15 H2 Z M9,1 V6 H14"),
            Stroke = iconBrush,
            StrokeThickness = 1.2,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0)
        };
        DockPanel.SetDock(icon, Dock.Left);
        headerPanel.Children.Add(icon);
        var nameText = new TextBlock
        {
            Text = System.IO.Path.GetFileName(path),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(nameText, Dock.Left);
        headerPanel.Children.Add(nameText);
        DockPanel.SetDock(save, Dock.Right);
        headerPanel.Children.Add(save);
        var encodingText = new TextBlock
        {
            Text = encoding ?? "",
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(encodingText, Dock.Right);
        headerPanel.Children.Add(encodingText);
        var pathText = new TextBlock
        {
            Text = path,
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path,
            Margin = new Thickness(0, 0, 10, 0)
        };
        headerPanel.Children.Add(pathText);
        var headerBorder = new Border
        {
            Height = 40,
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerPanel
        };
        DockPanel.SetDock(headerBorder, Dock.Top);
        panel.Children.Add(headerBorder);

        // 底部状态
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);

        // 编辑器
        Editor.Margin = new Thickness(12, 10, 12, 10);
        panel.Children.Add(Editor);
        Content = panel;

        Editor.Text = response.Text ?? "";
        status.Text = $"编码：{encoding ?? "未知"} · Ctrl+S 保存";
        Editor.TextChanged += (_, _) =>
        {
            dirty = true;
            Title = "* 文本 · " + path;
            status.Text = "有未保存修改";
            status.Foreground = ThemeBrush("WarningBrush", Color.FromRgb(0x8A, 0x5A, 0x00));
        };
        save.Click += async (_, _) => await SaveAsync();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                await SaveAsync();
            }
        };
        Closing += (_, e) =>
        {
            if (saving) { e.Cancel = true; status.Text = "正在保存，请稍候再关闭。"; return; }
            if (dirty && MessageBox.Show(this, "有未保存的文本修改，是否放弃并关闭？", "未保存内容", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }
    public async Task SaveAsync()
    {
        if (saving) return;
        saving = true;
        save.IsEnabled = false;
        Editor.IsReadOnly = true;
        status.Text = "正在保存…";
        status.Foreground = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61));
        try
        {
            var response = await client.CallAsync(new("save", path, Editor.Text, version, encoding));
            version = response.Version;
            dirty = false;
            Title = "文本 · " + path;
            status.Text = "已保存到服务端：" + DateTime.Now.ToString("HH:mm:ss");
            status.Foreground = ThemeBrush("SuccessBrush", Color.FromRgb(0x1A, 0x7F, 0x37));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "文本窗口保存失败；路径 {RemotePath}", path);
            status.Text = "保存失败，编辑内容仍保留：" + ex.Message;
            status.Foreground = ThemeBrush("DangerBrush", Color.FromRgb(0xB4, 0x23, 0x18));
        }
        finally { saving = false; save.IsEnabled = true; Editor.IsReadOnly = false; }
    }
    private Brush ThemeBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private Style? ThemeStyle(string key) => TryFindResource(key) as Style;
}
