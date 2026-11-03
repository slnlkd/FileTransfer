using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;
public sealed class ProcessViewer : Window
{
    private readonly RemoteFileClient client;
    private readonly string path;
    private readonly Ellipse stateDot = new()
    {
        Width = 8,
        Height = 8,
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock stateText = new()
    {
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock pidText = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detailText = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock timeText = new() { FontSize = 11 };
    private readonly StackPanel buttons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool busy;
    public ProcessViewer(RemoteFileClient client, string path)
    {
        ThemeLoader.Apply(this);
        this.client = client; this.path = path;
        Title = "程序运行状态 · " + path;
        Width = 740; Height = 330; MinWidth = 560; MinHeight = 280;
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
        var muted = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61));
        var ink = ThemeBrush("InkBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
        var iconBrush = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61));

        stateDot.Fill = muted;
        stateText.Foreground = ink;
        pidText.Foreground = ink;
        detailText.Foreground = ink;
        timeText.Foreground = muted;

        var panel = new DockPanel { Margin = new Thickness(12) };

        // 顶部文件信息
        var headerPanel = new DockPanel { Height = 40, LastChildFill = true };
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,3 H14 V13 H2 Z M4,6 H12 M4,9 H9"),
            Stroke = iconBrush,
            StrokeThickness = 1.2,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
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
        var pathText = new TextBlock
        {
            Text = path,
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path
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

        // 状态卡片
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal };
        stateRow.Children.Add(stateDot);
        stateRow.Children.Add(stateText);
        var details = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddDetailRow(details, "PID", pidText, 0);
        AddDetailRow(details, "详情", detailText, 1);
        AddDetailRow(details, "检查时间", timeText, 2);
        var cardStack = new StackPanel();
        cardStack.Children.Add(stateRow);
        cardStack.Children.Add(details);
        var card = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(16),
            Child = cardStack
        };

        // 操作按钮
        foreach (var action in new[] { ("刷新状态", "status"), ("停止程序", "stop"), ("启动程序", "start"), ("重新启动", "restart") })
        {
            var button = new Button { Content = action.Item1, Margin = new Thickness(4, 0, 0, 0) };
            string styleKey = action.Item2 switch
            {
                "start" => "PrimaryButton",
                "stop" => "DangerButton",
                _ => "SecondaryButton"
            };
            if (ThemeStyle(styleKey) is Style style) button.Style = style;
            button.Click += async (_, _) => await ExecuteAsync(action.Item2);
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(card);
        Content = panel;

        stateText.Text = "检查中…";
        Loaded += async (_, _) => { await ExecuteAsync("status"); timer.Start(); };
        timer.Tick += async (_, _) => await ExecuteAsync("status");
        Closed += (_, _) => timer.Stop();
    }
    private void AddDetailRow(Grid grid, string label, TextBlock value, int row)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61)),
            Margin = new Thickness(0, 0, 12, 4)
        };
        Grid.SetRow(labelText, row);
        grid.Children.Add(labelText);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }
    public async Task ExecuteAsync(string command)
    {
        if (busy) return;
        busy = true;
        buttons.IsEnabled = false;
        try
        {
            if (command == "restart") await client.CallAsync(new("stop", path));
            var response = await client.CallAsync(new(command == "restart" ? "start" : command, path));
            var state = response.Process;
            stateText.Text = state?.State ?? "未知";
            stateText.Foreground = ThemeBrush("InkBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
            stateDot.Fill = state?.State switch
            {
                "运行中" or "已启动" => ThemeBrush("SuccessBrush", Color.FromRgb(0x1A, 0x7F, 0x37)),
                "未运行" => ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61)),
                "状态未知" => ThemeBrush("WarningBrush", Color.FromRgb(0x8A, 0x5A, 0x00)),
                _ => ThemeBrush("MutedBrush", Color.FromRgb(0x61, 0x61, 0x61))
            };
            pidText.Text = state?.ProcessIds is { Length: > 0 } ids ? string.Join(", ", ids) : "—";
            detailText.Text = string.IsNullOrWhiteSpace(state?.Detail) ? "—" : state.Detail;
            detailText.Foreground = ThemeBrush("InkBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
            timeText.Text = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "程序管理窗口操作失败；命令 {Command}，路径 {RemotePath}", command, path);
            stateText.Text = "操作失败";
            stateText.Foreground = ThemeBrush("DangerBrush", Color.FromRgb(0xB4, 0x23, 0x18));
            stateDot.Fill = ThemeBrush("DangerBrush", Color.FromRgb(0xB4, 0x23, 0x18));
            detailText.Text = ex.Message;
            detailText.Foreground = ThemeBrush("DangerBrush", Color.FromRgb(0xB4, 0x23, 0x18));
            timeText.Text = DateTime.Now.ToString("HH:mm:ss");
        }
        finally { busy = false; buttons.IsEnabled = true; }
    }
    private Brush ThemeBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private Style? ThemeStyle(string key) => TryFindResource(key) as Style;
}
