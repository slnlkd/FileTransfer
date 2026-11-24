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
        SelectionOpacity = 0.9,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock status = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock position = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button save = new()
    {
        Content = "保存",
        ToolTip = "保存到服务端 (Ctrl+S)",
        Height = 28,
        MinWidth = 58,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border findBar;
    private readonly TextBox findBox;
    private readonly TextBlock findStatus;
    private readonly Canvas highlightLayer;
    private readonly RemoteFileClient client;
    private readonly string path;
    private string? version;
    private readonly string? encoding;
    private bool dirty;
    private bool saving;
    private bool suppressFind;
    private int findVersion;
    private readonly List<int> matchOffsets = [];
    private readonly List<int> lineStarts = [0];
    private ScrollViewer? editorScroll;
    private readonly Slider scrollBar = new()
    {
        Orientation = Orientation.Vertical,
        Width = 12,
        Minimum = 0,
        Maximum = 0,
        IsMoveToPointEnabled = true,
        Focusable = false,
        Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private bool syncingScrollBar;
    private readonly System.Windows.Threading.DispatcherTimer scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private double scrollFrom, scrollTarget;
    private int scrollElapsed;
    private const int SmoothScrollDuration = 140;
    private DateTime lastFindScrollUtc;
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
        status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = status });
        var saveStyle = ThemeStyle("PrimaryButton");
        if (saveStyle is not null) save.Style = saveStyle;
        save.SetResourceReference(Control.ForegroundProperty, "CanvasBrush");

        var panel = new DockPanel();

        // 顶部文件信息
        var headerPanel = new DockPanel { Height = 40, LastChildFill = true, Margin = new Thickness(10, 0, 10, 0) };
        var findButton = new Button
        {
            Content = "查找", Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Style = ThemeStyle("GhostButton"), ToolTip = "查找 (Ctrl+F)", Margin = new Thickness(0, 0, 10, 0)
        };
        findButton.Click += (_, _) => ShowFind();
        var wrap = new CheckBox
        {
            Content = "自动换行", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        wrap.Checked += (_, _) => SetWordWrap(true);
        wrap.Unchecked += (_, _) => SetWordWrap(false);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Children = { findButton, wrap, save } };
        DockPanel.SetDock(commands, Dock.Right);
        headerPanel.Children.Add(commands);
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,1 H9 L14,6 V15 H2 Z M9,1 V6 H14"),
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
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path,
            Margin = new Thickness(0, 0, 10, 0)
        };
        headerPanel.Children.Add(nameText);
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

        // 查找栏
        findBox = new TextBox
        {
            Height = 28, MinHeight = 28, Padding = new Thickness(8, 4, 8, 4),
            Style = ThemeStyle("RoundedTextInput"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(findBox, "查找文本");
        findStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 48
        };
        var findLabel = new TextBlock
        {
            Text = "查找",
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var previousFind = CreateFindButton("M4,10 L8,6 L12,10", "上一处 (Shift+Enter)");
        var nextFind = CreateFindButton("M4,6 L8,10 L12,6", "下一处 (Enter)");
        var closeFind = CreateFindButton("M4,4 L12,12 M12,4 L4,12", "关闭查找 (Esc)");
        var findGrid = new Grid();
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(findLabel, 0); findGrid.Children.Add(findLabel);
        Grid.SetColumn(findBox, 1); findGrid.Children.Add(findBox);
        Grid.SetColumn(findStatus, 2); findGrid.Children.Add(findStatus);
        Grid.SetColumn(previousFind, 3); findGrid.Children.Add(previousFind);
        Grid.SetColumn(nextFind, 4); findGrid.Children.Add(nextFind);
        Grid.SetColumn(closeFind, 5); findGrid.Children.Add(closeFind);
        findBar = new Border
        {
            Height = 36,
            Padding = new Thickness(8, 0, 8, 0),
            Background = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Visibility = Visibility.Collapsed,
            Child = findGrid
        };
        DockPanel.SetDock(findBar, Dock.Top);
        panel.Children.Add(findBar);
        findBox.TextChanged += (_, _) =>
        {
            if (suppressFind || findBar.Visibility != Visibility.Visible) return;
            FindFromStart();
        };
        previousFind.Click += (_, _) => { FindNext(false); findBox.Focus(); };
        nextFind.Click += (_, _) => { FindNext(true); findBox.Focus(); };
        closeFind.Click += (_, _) => HideFind();

        // 底部状态
        position.Foreground = muted;
        var metrics = new StackPanel { Orientation = Orientation.Horizontal };
        metrics.Children.Add(position);
        metrics.Children.Add(new Border { Width = 1, Height = 12, Background = line, Margin = new Thickness(10, 0, 10, 0) });
        metrics.Children.Add(new TextBlock
        {
            Text = encoding switch
            {
                "65001:plain" => "UTF-8",
                "65001:bom" => "UTF-8 (BOM)",
                "1200:bom" => "UTF-16 LE",
                "1201:bom" => "UTF-16 BE",
                "12000:bom" => "UTF-32 LE",
                "12001:bom" => "UTF-32 BE",
                "936:plain" => "GBK",
                null or "" => "编码未知",
                _ => encoding
            },
            FontSize = 11, Foreground = muted, VerticalAlignment = VerticalAlignment.Center
        });
        var statusPanel = new DockPanel();
        DockPanel.SetDock(metrics, Dock.Right);
        statusPanel.Children.Add(metrics);
        status.Margin = new Thickness(0, 0, 12, 0);
        statusPanel.Children.Add(status);
        var statusBar = new Border
        {
            Height = 30, Padding = new Thickness(10, 0, 10, 0), Background = header,
            BorderBrush = line, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusPanel
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        panel.Children.Add(statusBar);

        // 编辑器 + 查找高亮层（高亮在文字下方，文字保持清晰）
        Editor.Margin = new Thickness(0);
        Editor.SetResourceReference(TextBox.SelectionBrushProperty, "SelectionBrush");
        Editor.Background = Brushes.Transparent;
        Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        highlightLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        Editor.BorderThickness = new Thickness(0);
        Editor.Padding = new Thickness(10, 8, 10, 8);
        var editorHost = new Grid { Background = surface, ClipToBounds = true };
        editorHost.PreviewMouseWheel += Editor_MouseWheel;
        editorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(highlightLayer, 0);
        editorHost.Children.Add(highlightLayer);
        Grid.SetColumn(Editor, 0);
        editorHost.Children.Add(Editor);
        scrollBar.Style = ThemeStyle("VerticalTextScrollBarStyle");
        scrollBar.ValueChanged += (_, _) =>
        {
            if (!syncingScrollBar && editorScroll is not null)
            {
                scrollTimer.Stop();
                editorScroll.ScrollToVerticalOffset(scrollBar.Value);
            }
        };
        Grid.SetColumn(scrollBar, 1);
        editorHost.Children.Add(scrollBar);
        Editor.SizeChanged += (_, _) => UpdateScrollBar();
        scrollTimer.Tick += (_, _) => TickSmoothScroll();
        panel.Children.Add(editorHost);
        Content = panel;

        Editor.Text = response.Text ?? "";
        RebuildLineStarts();
        UpdatePosition();
        Editor.SelectionChanged += (_, _) => UpdatePosition();
        Dispatcher.BeginInvoke(new Action(UpdateScrollBar), System.Windows.Threading.DispatcherPriority.Background);
        status.Text = "就绪";
        Editor.TextChanged += (_, _) =>
        {
            dirty = true;
            Title = "* 文本 · " + path;
            status.Text = "有未保存修改";
            status.Foreground = ThemeBrush("WarningBrush", Color.FromRgb(0x8A, 0x5A, 0x00));
            RebuildLineStarts();
            UpdatePosition();
            if (findBar.IsVisible) RebuildMatches();
            findVersion++;
            highlightLayer.Children.Clear();
            Dispatcher.BeginInvoke(new Action(UpdateScrollBar), System.Windows.Threading.DispatcherPriority.Background);
        };
        save.Click += async (_, _) => await SaveAsync();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                ShowFind();
                return;
            }
            if (e.Key == Key.F3)
            {
                e.Handled = true;
                if (findBar.Visibility != Visibility.Visible) ShowFind();
                else FindNext(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                return;
            }
            if (findBar.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    HideFind();
                    return;
                }
                if (e.Key == Key.Enter && findBox.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    FindNext(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    return;
                }
            }
            if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                await SaveAsync();
            }
        };
        Closing += TextViewer_Closing;
        Loaded += TextViewer_Loaded;
        Closed += TextViewer_Closed;
    }
    private Button CreateFindButton(string geometry, string label)
    {
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Style = ThemeStyle("IconGlyph") };
        var button = new Button { Content = glyph, Style = ThemeStyle("IconButton"), ToolTip = label };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    private void SetWordWrap(bool enabled)
    {
        Editor.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Editor.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        findVersion++;
        highlightLayer.Children.Clear();
        Dispatcher.BeginInvoke(new Action(UpdateScrollBar), System.Windows.Threading.DispatcherPriority.Background);
    }
    private void RebuildLineStarts()
    {
        lineStarts.Clear();
        lineStarts.Add(0);
        string text = Editor.Text;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            if (text[index] is '\r' or '\n') lineStarts.Add(index + 1);
        }
    }
    private void UpdatePosition()
    {
        int caret = Editor.CaretIndex;
        int line = lineStarts.BinarySearch(caret);
        if (line < 0) line = ~line - 1;
        position.Text = $"第 {line + 1} 行，第 {caret - lineStarts[line] + 1} 列";
    }
    private void TextViewer_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (saving)
        {
            e.Cancel = true;
            return;
        }
        if (!dirty) return;

        var choice = Dialog.ConfirmSaveChanges(this, $"要保存对「{System.IO.Path.GetFileName(path)}」的修改吗？");
        e.Cancel = choice != MessageBoxResult.No;
        if (choice == MessageBoxResult.Yes)
        {
            // 先退出本次 Closing，再等待保存完成并关闭。
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await SaveAsync();
                if (!dirty) Close();
            });
        }
    }
    private void TextViewer_Loaded(object sender, RoutedEventArgs e)
    {
        Editor.ApplyTemplate();
        if (Editor.Template.FindName("PART_ContentHost", Editor) is not ScrollViewer scroll) return;
        if (ReferenceEquals(editorScroll, scroll)) return;
        if (editorScroll is not null) editorScroll.ScrollChanged -= EditorScrollChanged;
        editorScroll = scroll;
        editorScroll.ScrollChanged += EditorScrollChanged;
        UpdateScrollBar();
    }
    private void TextViewer_Closed(object? sender, EventArgs e)
    {
        scrollTimer.Stop();
        if (editorScroll is null) return;
        editorScroll.ScrollChanged -= EditorScrollChanged;
        editorScroll = null;
    }
    private void EditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateScrollBar();
        if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.HorizontalChange) < 0.01) return;
        // ScrollToLine 会触发一次滚动事件，短时间内不要清掉刚画好的高亮。
        if ((DateTime.UtcNow - lastFindScrollUtc).TotalMilliseconds < 250) return;
        findVersion++;
        highlightLayer.Children.Clear();
    }
    private void UpdateScrollBar()
    {
        if (editorScroll is null) return;
        syncingScrollBar = true;
        try
        {
            double max = Math.Max(0, editorScroll.ScrollableHeight);
            scrollBar.Maximum = max;
            scrollBar.LargeChange = Math.Max(1, editorScroll.ViewportHeight - 40);
            scrollBar.SmallChange = 40;
            scrollBar.Value = Math.Min(max, editorScroll.VerticalOffset);
            scrollBar.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { syncingScrollBar = false; }
    }
    private void Editor_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (editorScroll is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        // 日志通常很长，一次滚轮约 35% 视口，并用缓动动画让滚动更顺滑。
        double step = Math.Max(24, editorScroll.ViewportHeight * 0.35) * (e.Delta > 0 ? -1 : 1);
        StartSmoothScroll(editorScroll.VerticalOffset + step);
    }
    private void StartSmoothScroll(double target)
    {
        if (editorScroll is null) return;
        scrollFrom = editorScroll.VerticalOffset;
        scrollTarget = Math.Clamp(target, 0, editorScroll.ScrollableHeight);
        scrollElapsed = 0;
        scrollTimer.Stop();
        scrollTimer.Start();
    }
    private void TickSmoothScroll()
    {
        if (editorScroll is null) { scrollTimer.Stop(); return; }
        scrollElapsed += 15;
        double t = Math.Min(1, scrollElapsed / (double)SmoothScrollDuration);
        double eased = 1 - Math.Pow(1 - t, 3);
        editorScroll.ScrollToVerticalOffset(scrollFrom + (scrollTarget - scrollFrom) * eased);
        if (t >= 1) scrollTimer.Stop();
    }
    private void ShowFind()
    {
        bool wasHidden = findBar.Visibility != Visibility.Visible;
        findBar.Visibility = Visibility.Visible;
        if (wasHidden)
        {
            suppressFind = true;
            if (!string.IsNullOrEmpty(Editor.SelectedText)) findBox.Text = Editor.SelectedText;
            suppressFind = false;
        }
        findBox.Focus();
        findBox.SelectAll();
        FindFromStart();
    }
    private void HideFind()
    {
        findBar.Visibility = Visibility.Collapsed;
        findVersion++;
        highlightLayer.Children.Clear();
        Editor.Focus();
    }
    private void FindFromStart()
    {
        RebuildMatches();
        Editor.Select(0, 0);
        FindNext(true);
    }
    private void RebuildMatches()
    {
        matchOffsets.Clear();
        string needle = findBox.Text;
        if (needle.Length == 0) { SetFindStatus("", false); return; }
        string text = Editor.Text;
        int start = 0;
        while ((start = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            matchOffsets.Add(start);
            start += needle.Length;
        }
        SetFindStatus(matchOffsets.Count == 0 ? "无匹配" : $"{matchOffsets.Count} 处匹配", matchOffsets.Count == 0);
    }
    private void FindNext(bool forward)
    {
        if (findBar.Visibility != Visibility.Visible) { ShowFind(); return; }
        int version = ++findVersion;
        string needle = findBox.Text;
        if (matchOffsets.Count == 0) { highlightLayer.Children.Clear(); return; }
        int start = forward ? Editor.SelectionStart + Editor.SelectionLength : Editor.SelectionStart - 1;
        int match = matchOffsets.BinarySearch(start);
        if (match < 0) match = forward ? ~match : ~match - 1;
        if (match == matchOffsets.Count) match = 0;
        if (match < 0) match = matchOffsets.Count - 1;
        int index = matchOffsets[match];
        Editor.CaretIndex = index;
        Editor.Select(index, needle.Length);
        int line = Editor.GetLineIndexFromCharacterIndex(index);
        lastFindScrollUtc = DateTime.UtcNow;
        if (line >= 0) Editor.ScrollToLine(line);
        Editor.UpdateLayout();
        ShowMatchHighlight(index, needle.Length);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (version == findVersion) ShowMatchHighlight(index, needle.Length);
        }), System.Windows.Threading.DispatcherPriority.Background);
        SetFindStatus($"{match + 1} / {matchOffsets.Count}", false);
    }
    private void ShowMatchHighlight(int index, int length)
    {
        highlightLayer.Children.Clear();
        string text = Editor.Text;
        if (index < 0 || length <= 0 || index >= text.Length) return;
        int end = Math.Min(index + length, text.Length);
        int firstLine = Math.Max(Editor.GetLineIndexFromCharacterIndex(index), Editor.GetFirstVisibleLineIndex());
        int lastLine = Math.Min(Editor.GetLineIndexFromCharacterIndex(end - 1), Editor.GetLastVisibleLineIndex());
        for (int line = firstLine; line >= 0 && line <= lastLine; line++)
        {
            int lineStart = Editor.GetCharacterIndexFromLineIndex(line);
            int lineEnd = Math.Min(end, lineStart + Editor.GetLineLength(line));
            int matchStart = Math.Max(index, lineStart);
            if (matchStart >= lineEnd) continue;
            Rect start = Editor.GetRectFromCharacterIndex(matchStart, false);
            if (start.IsEmpty) continue;
            Rect finish = Editor.GetRectFromCharacterIndex(lineEnd - 1, true);
            var highlight = new System.Windows.Shapes.Rectangle
            {
                Fill = Editor.SelectionBrush,
                Width = !finish.IsEmpty && Math.Abs(finish.Top - start.Top) < 1
                    ? Math.Max(1, finish.Right - start.Left) : Math.Max(1, start.Width),
                Height = Math.Max(1, start.Height)
            };
            Canvas.SetLeft(highlight, start.Left);
            Canvas.SetTop(highlight, start.Top);
            highlightLayer.Children.Add(highlight);
        }
    }
    private void SetFindStatus(string text, bool error)
    {
        findStatus.Text = text;
        findStatus.Foreground = ThemeBrush(error ? "DangerBrush" : "MutedBrush",
            error ? Color.FromRgb(0xB4, 0x23, 0x18) : Color.FromRgb(0x61, 0x61, 0x61));
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
