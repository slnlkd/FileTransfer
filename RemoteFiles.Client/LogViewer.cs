using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteFiles.Desktop;
using Serilog;

namespace RemoteFiles.Client;

public sealed class LogViewer : Window
{
    private sealed record LogFile(string Name, string Path);
    private readonly string directory;
    private readonly ListBox files = new() { DisplayMemberPath = nameof(LogFile.Name), BorderThickness = new Thickness(0) };
    private readonly TextBox content = new()
    {
        IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0), Padding = new Thickness(10), IsUndoEnabled = false
    };
    private readonly TextBlock status = new() { Margin = new Thickness(12, 7, 12, 7), TextTrimming = TextTrimming.CharacterEllipsis };
    private CancellationTokenSource? loading;
    private bool closed;
    private bool refreshing;

    public LogViewer(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Title = "客户端日志";
        Width = 1100; Height = 720; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        ThemeLoader.Apply(this);
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        foreach (var control in new Control[] { files, content })
        {
            control.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            control.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        }
        System.Windows.Automation.AutomationProperties.SetName(files, "日志文件列表");
        System.Windows.Automation.AutomationProperties.SetName(content, "日志内容");
        files.SelectionChanged += async (_, _) => { if (!refreshing) await LoadSelectedAsync(); };
        var refresh = new Button { Content = "刷新", Style = (Style)FindResource("ToolbarButton") };
        refresh.Click += (_, _) => RefreshFiles();
        var copy = new Button { Content = "复制路径", Style = (Style)FindResource("ToolbarButton"), Margin = new Thickness(6, 0, 0, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText((files.SelectedItem as LogFile)?.Path ?? this.directory); status.Text = "已复制日志路径"; }
            catch (Exception ex) { status.Text = "复制路径失败：" + ex.Message; }
        };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4), Children = { refresh, copy } };
        var path = new TextBlock
        {
            Text = this.directory, ToolTip = this.directory, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0)
        };
        path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var toolbar = new DockPanel();
        DockPanel.SetDock(commands, Dock.Right); toolbar.Children.Add(commands); toolbar.Children.Add(path);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var listBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = files };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        body.Children.Add(listBorder); Grid.SetColumn(content, 1); body.Children.Add(content);
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status);
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        layout.Children.Add(body); Content = layout;
        Loaded += (_, _) => RefreshFiles();
        Closed += (_, _) => { closed = true; loading?.Cancel(); };
    }

    private void RefreshFiles()
    {
        refreshing = true;
        try
        {
            Directory.CreateDirectory(directory);
            string? selected = (files.SelectedItem as LogFile)?.Path;
            var entries = new DirectoryInfo(directory).EnumerateFiles("*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name)
                .Take(200).Select(file => new LogFile(file.Name, file.FullName)).ToArray();
            files.ItemsSource = entries;
            if (entries.Length == 0)
            {
                content.Clear(); status.Text = "暂无日志文件";
            }
            else files.SelectedItem = entries.FirstOrDefault(file => file.Path == selected) ?? entries[0];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取日志文件列表失败；目录 {Directory}", directory);
            status.Text = "读取日志列表失败：" + ex.Message;
        }
        finally { refreshing = false; }
        _ = LoadSelectedAsync();
    }

    private async Task LoadSelectedAsync()
    {
        loading?.Cancel();
        if (files.SelectedItem is not LogFile file) return;
        using var source = new CancellationTokenSource();
        loading = source;
        var token = source.Token;
        content.Clear(); status.Text = "正在读取 " + file.Name;
        try
        {
            var text = await Task.Run(async () =>
            {
                await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
                long offset = Math.Max(0, stream.Length - 512 * 1024);
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                if (offset > 0) await reader.ReadLineAsync(token); // 跳过截断的第一行，避免 UTF-8 半字符。
                string tail = await reader.ReadToEndAsync(token);
                return string.Join(Environment.NewLine, tail.Replace("\r\n", "\n").Split('\n').TakeLast(2000));
            }, token);
            if (closed || token.IsCancellationRequested) return;
            content.Text = text;
            content.ScrollToEnd();
            status.Text = file.Name + " · 只读，显示最近最多 2000 行 · 点击刷新读取最新内容";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closed || token.IsCancellationRequested) return;
            Log.Warning(ex, "读取日志失败；文件 {File}", file.Path);
            status.Text = "读取日志失败：" + ex.Message;
        }
        finally { if (ReferenceEquals(loading, source)) loading = null; }
    }
}
