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
        SelectionBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0xCB, 0xE3)),
        SelectionOpacity = 0.9,
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
    private ScrollViewer? editorScroll;
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

        // 查找栏
        findBox = new TextBox
        {
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
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
        var previousFind = new Button { Content = "上一个", Height = 26, Margin = new Thickness(0, 0, 4, 0) };
        var nextFind = new Button { Content = "下一个", Height = 26, Margin = new Thickness(0, 0, 4, 0) };
        var closeFind = new Button { Content = "关闭", Height = 26 };
        var secondaryStyle = ThemeStyle("SecondaryButton");
        if (secondaryStyle is not null) { previousFind.Style = secondaryStyle; nextFind.Style = secondaryStyle; }
        var ghostStyle = ThemeStyle("GhostButton");
        if (ghostStyle is not null) closeFind.Style = ghostStyle;
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
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);

        // 编辑器 + 查找高亮层（高亮在文字下方，文字保持清晰）
        Editor.Margin = new Thickness(0);
        Editor.Background = Brushes.Transparent;
        highlightLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        var editorHost = new Grid { Background = surface, Margin = new Thickness(12, 10, 12, 10), ClipToBounds = true };
        editorHost.Children.Add(highlightLayer);
        editorHost.Children.Add(Editor);
        panel.Children.Add(editorHost);
        Content = panel;

        Editor.Text = response.Text ?? "";
        status.Text = $"编码：{encoding ?? "未知"} · Ctrl+S 保存";
        Editor.TextChanged += (_, _) =>
        {
            dirty = true;
            Title = "* 文本 · " + path;
            status.Text = "有未保存修改";
            status.Foreground = ThemeBrush("WarningBrush", Color.FromRgb(0x8A, 0x5A, 0x00));
            findVersion++;
            highlightLayer.Children.Clear();
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
        Closing += (_, e) =>
        {
            if (saving) { e.Cancel = true; status.Text = "正在保存，请稍候再关闭。"; return; }
            if (dirty && MessageBox.Show(this, "有未保存的文本修改，是否放弃并关闭？", "未保存内容", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Loaded += TextViewer_Loaded;
        Closed += TextViewer_Closed;
    }
    private void TextViewer_Loaded(object sender, RoutedEventArgs e)
    {
        Editor.ApplyTemplate();
        if (Editor.Template.FindName("PART_ContentHost", Editor) is not ScrollViewer scroll) return;
        if (ReferenceEquals(editorScroll, scroll)) return;
        if (editorScroll is not null) editorScroll.ScrollChanged -= EditorScrollChanged;
        editorScroll = scroll;
        editorScroll.ScrollChanged += EditorScrollChanged;
    }
    private void TextViewer_Closed(object? sender, EventArgs e)
    {
        if (editorScroll is null) return;
        editorScroll.ScrollChanged -= EditorScrollChanged;
        editorScroll = null;
    }
    private void EditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.HorizontalChange) < 0.01) return;
        // ScrollToLine 会触发一次滚动事件，短时间内不要清掉刚画好的高亮。
        if ((DateTime.UtcNow - lastFindScrollUtc).TotalMilliseconds < 250) return;
        findVersion++;
        highlightLayer.Children.Clear();
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
        Editor.Select(0, 0);
        FindNext(true);
    }
    private void FindNext(bool forward)
    {
        if (findBar.Visibility != Visibility.Visible) { ShowFind(); return; }
        int version = ++findVersion;
        string needle = findBox.Text;
        if (needle.Length == 0) { highlightLayer.Children.Clear(); SetFindStatus("", false); return; }
        string text = Editor.Text;
        if (text.Length == 0) { highlightLayer.Children.Clear(); SetFindStatus("无匹配", true); return; }
        int start = forward ? Editor.SelectionStart + Editor.SelectionLength : Editor.SelectionStart - 1;
        if (start < 0) start = text.Length - 1;
        if (start >= text.Length) start = 0;
        int index = forward
            ? text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase)
            : text.LastIndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = forward
                ? text.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase)
                : text.LastIndexOf(needle, text.Length - 1, StringComparison.OrdinalIgnoreCase);
        }
        if (index < 0) { highlightLayer.Children.Clear(); SetFindStatus("无匹配", true); return; }
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
        SetFindStatus(line >= 0 ? $"第 {line + 1} 行" : "已找到", false);
    }
    private void ShowMatchHighlight(int index, int length)
    {
        highlightLayer.Children.Clear();
        string text = Editor.Text;
        if (index < 0 || length <= 0 || index >= text.Length) return;
        Rect start = Editor.GetRectFromCharacterIndex(index, false);
        if (start.IsEmpty) return;
        var highlight = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(Color.FromRgb(0xA9, 0xCB, 0xE3)),
            StrokeThickness = 0,
            RadiusX = 0,
            RadiusY = 0,
            IsHitTestVisible = false
        };
        int end = Math.Min(index + length, text.Length);
        Rect endRect = Editor.GetRectFromCharacterIndex(Math.Max(index, end - 1), true);
        highlight.Width = !endRect.IsEmpty && Math.Abs(endRect.Top - start.Top) < 1
            ? Math.Max(1, endRect.Right - start.Left)
            : Math.Max(1, start.Width);
        highlight.Height = Math.Max(1, start.Height);
        Canvas.SetLeft(highlight, start.Left);
        Canvas.SetTop(highlight, start.Top);
        highlightLayer.Children.Add(highlight);
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
