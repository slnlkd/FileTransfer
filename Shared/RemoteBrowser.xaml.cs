using System.IO;
using System.IO.Compression;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;
public partial class RemoteBrowser : UserControl
{
    private RemoteFileClient? client;
    private string directory = "";
    private bool busy;
    private bool rebuilding;
    private System.Collections.ObjectModel.ObservableCollection<RemoteEntry> entries = [];
    private const int PageSize = 500;
    private int totalCount;
    private bool allLoaded = true;
    private bool loadingMore;
    private readonly Dictionary<string, (DateTime At, RemoteEntry[] Entries)> unfilteredCache = [];
    private int navigationVersion;
    private CancellationTokenSource? operationCancel;
    private Window? hostWindow;
    private int pendingUp;
    private bool navigatingUp;
    private string? listFilter;
    private Func<string, string?>? localEmptyText = null;
    private ScrollViewer? fileScroll;
    private bool scrollHooked;
    private bool scrollResetPending;
    private Point dragOrigin;
    private bool dragArmed;
    public bool IsBusy => busy;
    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(FileGrid.ItemsSource) is not ListCollectionView view) return;
        e.Handled = true;
        string member = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(member)) { view.CustomSort = null; return; }
        // 分页状态下先取回全部条目，避免只对当前页排序造成顺序错乱。
        if (!allLoaded)
        {
            SetStatus("正在加载全部条目…", "MutedBrush");
            await LoadAllPagesAsync();
        }
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        view.CustomSort = new EntryComparer(member, direction);
        e.Column.SortDirection = direction;
    }
    private sealed class EntryComparer(string member, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var left = (RemoteEntry)x!;
            var right = (RemoteEntry)y!;
            // 与资源管理器一致：文件夹始终排在文件前，组内按当前列排序。
            if (left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;
            int result = member switch
            {
                "Name" => NaturalComparer.Compare(left.Name, right.Name),
                "Kind" => string.Compare(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase),
                "Size" => left.Length.CompareTo(right.Length),
                "CreatedLocal" => left.CreatedLocal.CompareTo(right.CreatedLocal),
                "ModifiedLocal" => left.ModifiedLocal.CompareTo(right.ModifiedLocal),
                _ => 0
            };
            return direction == ListSortDirection.Descending ? -Math.Sign(result) : Math.Sign(result);
        }
    }
    public RemoteBrowser()
    {
        InitializeComponent();
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        var style = new Style(typeof(DataGridRow));
        var baseRowStyle = TryFindResource("DataGridRowStyle") as Style ?? Application.Current?.TryFindResource("DataGridRowStyle") as Style;
        if (baseRowStyle is not null) style.BasedOn = baseRowStyle;
        style.Setters.Add(new EventSetter(ContextMenuOpeningEvent, new ContextMenuEventHandler(Row_ContextMenuOpening)));
        style.Setters.Add(new EventSetter(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(Row_RightClick)));
        // 占位菜单确保 WPF 会引发 ContextMenuOpening；打开时按当前行生成真实菜单。
        style.Setters.Add(new Setter(ContextMenuProperty, new ContextMenu()));
        FileGrid.RowStyle = style;
        UpdateEmptyState();
        BuildLocalFilters(FilterHost);
        FileGrid.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Grid_MouseLeftButtonDown), true);
        FileGrid.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(Grid_MouseMove), true);
        FileGrid.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Grid_MouseLeftButtonUp), true);
        Loaded += Browser_Loaded;
        Unloaded += Browser_Unloaded;
    }
    public async Task ConnectAsync(RemoteFileClient endpoint)
    {
        if (busy) throw new IOException("当前操作尚未结束，请稍后重新连接");
        client = endpoint; directory = "";
        unfilteredCache.Clear();
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        await NavigateAsync("", true);
    }
    public void Disconnect()
    {
        operationCancel?.Cancel();
        client = null;
        entries = [];
        totalCount = 0; allLoaded = true;
        navigationVersion++;
        scrollResetPending = false;
        unfilteredCache.Clear();
        FolderTree.Items.Clear();
        FileGrid.ItemsSource = null;
        PathText.Inlines.Clear();
        PathText.Text = "尚未连接";
        PathText.ToolTip = null;
        EndpointText.Text = "";
        ItemCountText.Text = "";
        HideBusy();
        HideProgress();
        SetStatus("尚未连接", "MutedBrush");
        UpdateEmptyState();
    }
    private RemoteFileClient Endpoint => client ?? throw new IOException("请先连接服务端");
    private async Task NavigateAsync(string path, bool rebuild = false, CancellationToken ct = default)
    {
        bool ownedBusy = !busy;
        if (ownedBusy) ShowBusy("正在读取目录…");
        else BusyText.Text = "正在读取目录…";
        try
        {
            var response = await Endpoint.CallAsync(new("list", path, Filter: listFilter, Take: PageSize), ct);
            if (!string.Equals(directory, path, StringComparison.Ordinal))
            {
                foreach (var column in FileGrid.Columns) column.SortDirection = null;
                FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
            }
            var nameDirection = FileGrid.Columns[0].SortDirection;
            string previous = directory;
            directory = path;
            navigationVersion++;
            var page = response.Entries ?? [];
            entries = new(page);
            totalCount = response.Total > 0 ? response.Total : page.Length;
            allLoaded = page.Length >= totalCount;
            FileGrid.ItemsSource = entries;
            // 切换目录后回到列表顶部；同一目录刷新保留当前位置。
            if (!string.Equals(previous, path, StringComparison.Ordinal)) ResetFileScroll();
            if (nameDirection is { } direction && CollectionViewSource.GetDefaultView(entries) is ListCollectionView view)
            {
                view.CustomSort = new EntryComparer("Name", direction);
                FileGrid.Columns[0].SortDirection = direction;
            }
            UpdatePath(response);
            UpdateCountText();
            SetStatus(response.Warning ?? (entries.Count == 0 ? EmptyTextFor(directory) : "就绪"),
                response.Warning is null ? "MutedBrush" : "WarningBrush");
            UpdateEmptyState();
            if (rebuild || FolderTree.Items.Count == 0)
            {
                rebuilding = true;
                try
                {
                    FolderTree.Items.Clear();
                    var root = MakeNode(new(response.RootName ?? "共享根目录", "", true, 0, default, default));
                    SetChildren(root, await UnfilteredEntriesAsync(path, ct));
                    root.IsExpanded = true;
                    FolderTree.Items.Add(root);
                }
                finally { rebuilding = false; }
            }
            else if (FindNode(FolderTree.Items, path) is TreeViewItem { IsExpanded: true } node)
            {
                // 目录树始终展示未过滤的完整内容，过滤只作用于右侧列表；折叠的节点不预加载。
                SetChildren(node, await UnfilteredEntriesAsync(path, ct));
            }
        }
        finally { if (ownedBusy) HideBusy(); }
    }
    private void UpdateCountText() => ItemCountText.Text = allLoaded ? $"{entries.Count} 项" : $"{entries.Count} / {totalCount} 项";
    // 目录树始终使用未过滤结果，且不受分页影响；结果短时间缓存，避免来回切换时重复请求。
    private async Task<RemoteEntry[]> UnfilteredEntriesAsync(string path, CancellationToken ct)
    {
        if (unfilteredCache.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(10)) return cached.Entries;
        var response = await Endpoint.CallAsync(new("list", path), ct);
        var all = response.Entries ?? [];
        unfilteredCache[path] = (DateTime.UtcNow, all);
        return all;
    }
    private async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (loadingMore || allLoaded || client is null) return;
        loadingMore = true;
        int version = navigationVersion;
        try
        {
            var response = await Endpoint.CallAsync(new("list", directory, Filter: listFilter, Skip: entries.Count, Take: PageSize), ct);
            if (version != navigationVersion) return;
            if (response.Total > 0) totalCount = response.Total;
            var page = response.Entries ?? [];
            foreach (var entry in page) entries.Add(entry);
            allLoaded = page.Length == 0 || entries.Count >= totalCount;
            UpdateCountText();
            UpdateEmptyState();
            if (CollectionViewSource.GetDefaultView(entries) is ListCollectionView view && view.CustomSort is not null) view.Refresh();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载更多条目失败；目录 {RemoteDirectory}", directory);
            SetStatus("加载更多失败：" + ex.Message, "DangerBrush");
        }
        finally { loadingMore = false; }
    }
    private async Task LoadAllPagesAsync()
    {
        while (!allLoaded && client is not null)
        {
            int before = entries.Count;
            await LoadMoreAsync();
            if (entries.Count == before) break;
        }
    }
    private void FileScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 切换目录后的第一次滚动事件来自旧位置，重置完成前不要触发加载更多。
        if (scrollResetPending || busy || loadingMore || allLoaded) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 200) return;
        _ = LoadMoreAsync();
    }
    private void UpdatePath(RemoteResponse response)
    {
        string rootName = response.RootName ?? "共享根目录";
        PathText.Inlines.Clear();
        PathText.Inlines.Add(new Run(rootName) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(directory))
        {
            PathText.Inlines.Add(new Run("  /  ") { Foreground = ThemeBrush("SubtleBrush") });
            PathText.Inlines.Add(new Run(directory.Replace("/", "  /  ")));
        }
        PathText.ToolTip = $"{Endpoint.Host}:{Endpoint.Port} / {rootName}" + (directory.Length == 0 ? "" : " / " + directory);
        EndpointText.Text = $"{Endpoint.Host}:{Endpoint.Port}";
    }
    private void UpdateEmptyState()
    {
        bool connected = client != null;
        EmptyState.Visibility = connected && entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = connected ? EmptyTextFor(directory) : "连接后可浏览共享目录";
        EmptyUploadButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }
    private string EmptyTextFor(string path) => localEmptyText?.Invoke(path) ?? "此文件夹为空";
    private void ResetFileScroll()
    {
        fileScroll ??= FindVisualChild<ScrollViewer>(FileGrid);
        if (fileScroll is null) return;
        scrollResetPending = true;
        fileScroll.ScrollToTop();
        // 等待新条目完成布局后再设置一次，避免虚拟化把偏移量恢复成旧位置。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            fileScroll?.ScrollToTop();
            scrollResetPending = false;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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
    // 由本地个性化扩展实现；未实现时界面不显示额外过滤控件。
    partial void BuildLocalFilters(StackPanel host);
    // 过滤值变化后重新加载右侧列表；目录树不受影响。
    private async void SetLocalFilter(string? value)
    {
        if (string.Equals(listFilter, value, StringComparison.Ordinal)) return;
        listFilter = value;
        if (client is null) return;
        await ExecuteAsync(() => NavigateAsync(directory));
        // 过滤条件变化会改变列表内容，同样回到顶部。
        ResetFileScroll();
    }
    private static TreeViewItem? FindNode(ItemCollection items, string path)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is RemoteEntry entry && entry.Path == path) return item;
            if (FindNode(item.Items, path) is { } child) return child;
        }
        return null;
    }
    private TreeViewItem MakeNode(RemoteEntry entry)
    {
        var node = new TreeViewItem { Header = CreateNodeHeader(entry), Tag = entry };
        node.ContextMenu = BuildMenu(entry);
        node.PreviewMouseRightButtonDown += (_, e) =>
        {
            // 菜单绑定点击节点，保持当前目录，避免弹出菜单前触发异步目录导航。
            if (!ReferenceEquals(ItemsControl.ContainerFromElement(null, e.OriginalSource as DependencyObject), node)) return;
            e.Handled = true;
        };
        if (entry.IsDirectory)
        {
            node.Items.Add("加载中……");
            node.Expanded += async (sender, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, node) || rebuilding) return;
                await ExecuteAsync(async () => SetChildren(node, await UnfilteredEntriesAsync(entry.Path, CancellationToken.None)));
            };
        }
        return node;
    }
    private FrameworkElement CreateNodeHeader(RemoteEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (FindResource("FileIcon") is FileIconConverter converter)
            icon.Source = converter.Convert(entry, typeof(ImageSource), null!, CultureInfo.InvariantCulture) as ImageSource;
        grid.Children.Add(icon);
        var text = new TextBlock
        {
            Text = entry.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Name
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
    private void SetChildren(TreeViewItem node, IEnumerable<RemoteEntry> children)
    {
        node.Items.Clear();
        foreach (var child in children) node.Items.Add(MakeNode(child));
    }
    private async void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (rebuilding || e.NewValue is not TreeViewItem { Tag: RemoteEntry entry } || !entry.IsDirectory) return;
        await ExecuteAsync(() => NavigateAsync(entry.Path));
    }
    private async void Tree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderTree.SelectedItem is TreeViewItem { Tag: RemoteEntry entry } && !entry.IsDirectory)
            await ExecuteAsync(() => OpenAsync(entry));
    }
    private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FileGrid, e.OriginalSource as DependencyObject) is DataGridRow row && row.Item is RemoteEntry entry)
            await ExecuteAsync(() => OpenAsync(entry));
    }
    public async Task OpenAsync(RemoteEntry entry)
    {
        Log.Information("打开远程文件；路径 {RemotePath}，类型 {Kind}", entry.Path, entry.Kind);
        BusyText.Text = "正在打开文件…";
        if (entry.IsDirectory) { await NavigateAsync(entry.Path); return; }
        Window viewer;
        if (entry.IsText) viewer = new TextViewer(Endpoint, entry.Path, await Endpoint.CallAsync(new("text", entry.Path)));
        else if (entry.IsImage)
        {
            string parent = ParentPath(entry.Path);
            var siblings = (await Endpoint.CallAsync(new("list", parent))).Entries ?? [];
            viewer = new ImageViewer(Endpoint, siblings.Where(e => e.IsImage).OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), entry.Path);
        }
        else if (entry.IsExe) viewer = new ProcessViewer(Endpoint, entry.Path);
        else { SetStatus("此文件类型暂不提供预览或编辑：" + entry.Kind, "MutedBrush"); return; }
        viewer.Owner = Window.GetWindow(this); viewer.Show();
        SetStatus("已打开 " + entry.Name, "MutedBrush");
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        // 刷新会丢弃未过滤缓存，确保目录树看到服务端最新内容。
        unfilteredCache.Clear();
        // 根目录重建可移除树中的过期节点；目录内刷新只更新当前节点。
        await NavigateAsync(directory, directory.Length == 0);
    });
    private async void Parent_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => NavigateAsync(ParentPath(directory)));
    private static string ParentPath(string path) { int index = path.LastIndexOf('/'); return index < 0 ? "" : path[..index]; }
    private void Browser_Loaded(object sender, RoutedEventArgs e)
    {
        hostWindow = Window.GetWindow(this);
        if (!scrollHooked)
        {
            fileScroll ??= FindVisualChild<ScrollViewer>(FileGrid);
            if (fileScroll is not null) { fileScroll.ScrollChanged += FileScroll_ScrollChanged; scrollHooked = true; }
        }
        if (hostWindow is null) return;
        hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        hostWindow.PreviewKeyDown += HostWindow_PreviewKeyDown;
    }
    private void Browser_Unloaded(object sender, RoutedEventArgs e)
    {
        if (fileScroll is not null) { fileScroll.ScrollChanged -= FileScroll_ScrollChanged; fileScroll = null; }
        scrollHooked = false;
        if (hostWindow is null) return;
        hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        hostWindow = null;
    }
    private async void HostWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.A or Key.C)
        {
            // 输入框里的 Ctrl+A/C 继续用于编辑文字。
            if (Keyboard.FocusedElement is TextBox) return;
            if (!FileArea.IsMouseOver && !FileGrid.IsKeyboardFocusWithin) return;
            if (e.Key == Key.A)
            {
                e.Handled = true;
                await SelectAllAsync();
                return;
            }
            var selected = FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
            if (selected.Length == 0) return;
            e.Handled = true;
            await CopyToClipboardAsync(selected);
            return;
        }
        if (e.Key != Key.Back) return;
        // 连续退格导航期间焦点可能被禁用移走，此时仍然把退格当作“上一级”。
        if (navigatingUp)
        {
            e.Handled = true;
            await HandleBackspaceAsync();
            return;
        }
        // 输入框里的退格仍然用于编辑文字，不触发目录返回。
        if (Keyboard.FocusedElement is TextBox) return;
        // 与资源管理器一致：只要鼠标在右侧文件区域，或列表获得焦点，退格就返回上一级。
        if (!FileArea.IsMouseOver && !FileGrid.IsKeyboardFocusWithin) return;
        e.Handled = true;
        await HandleBackspaceAsync();
    }
    private async void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SelectAllAsync();
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var selected = FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
            if (selected.Length == 0) return;
            e.Handled = true;
            await CopyToClipboardAsync(selected);
            return;
        }
        if (e.Key != Key.Back) return;
        e.Handled = true;
        await HandleBackspaceAsync();
    }
    private async Task SelectAllAsync()
    {
        if (!allLoaded)
        {
            SetStatus("正在加载全部条目…", "MutedBrush");
            await LoadAllPagesAsync();
        }
        FileGrid.Focus();
        FileGrid.SelectAll();
        SetStatus($"已选择 {FileGrid.SelectedItems.Count} 项", "MutedBrush");
    }
    private async Task HandleBackspaceAsync()
    {
        if (client is null) return;
        if (busy && !navigatingUp) return;
        pendingUp++;
        if (busy) return;
        await DrainUpAsync();
    }
    private async Task DrainUpAsync()
    {
        if (navigatingUp) return;
        navigatingUp = true;
        try
        {
            while (pendingUp > 0 && !string.IsNullOrEmpty(directory))
            {
                pendingUp--;
                await ExecuteAsync(() => NavigateAsync(ParentPath(directory)));
                FileGrid.Focus();
            }
            pendingUp = 0;
        }
        finally { navigatingUp = false; }
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (client == null) { SetStatus("请先连接服务端", "DangerBrush"); return; }
        var dialog = new OpenFileDialog { Multiselect = true, Title = "上传文件到服务端当前目录" };
        if (dialog.ShowDialog() != true) return;
        var endpoint = Endpoint; string targetDirectory = directory;
        await ExecuteAsync(async ct =>
        {
            BusyText.Text = "正在上传…";
            ShowProgress("准备上传");
            int uploaded = 0;
            foreach (string local in dialog.FileNames)
            {
                string name = Path.GetFileName(local);
                bool exists = entries.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (exists && MessageBox.Show(Window.GetWindow(this), $"服务端已存在 {name}，是否覆盖？", "上传覆盖", MessageBoxButton.YesNo) != MessageBoxResult.Yes) continue;
                string target = targetDirectory.Length == 0 ? name : targetDirectory + "/" + name;
                ShowProgress(name);
                var progress = new Progress<double>(value =>
                {
                    SetProgress(value, $"{name} {value:F1}%");
                    SetStatus("正在上传 " + name, "MutedBrush");
                });
                await endpoint.UploadAsync(local, target, exists, progress, ct);
                uploaded++;
            }
            unfilteredCache.Clear();
            await NavigateAsync(targetDirectory, false, ct);
            SetProgress(100, "上传完成");
            SetStatus(uploaded > 0 ? "上传完成" : "未上传文件", uploaded > 0 ? "SuccessBrush" : "MutedBrush");
        }, cancellable: true);
    }
    private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        bool blocked = FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(source) is not null;
        bool onRow = ItemsControl.ContainerFromElement(FileGrid, source) is DataGridRow;
        dragArmed = !busy && !blocked && (onRow || (FileGrid.SelectedItems.Count > 0 && FileGrid.IsMouseOver));
        dragOrigin = e.GetPosition(FileGrid);
        Log.Debug("拖拽准备；行内 {OnRow}，阻止 {Blocked}，选中 {Count}", onRow, blocked, FileGrid.SelectedItems.Count);
    }
    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => dragArmed = false;
    private void Grid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!dragArmed || busy || client is null || e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(FileGrid);
        if (Math.Abs(current.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragArmed = false;
        var selected = FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
        if (selected.Length == 0) return;
        // Windows 的 UIPI 会阻止管理员进程向普通资源管理器拖放，改用复制更稳妥。
        if (IsElevated())
        {
            SetStatus("以管理员运行时 Windows 禁止拖出，已改为复制，请在本地按 Ctrl+V", "WarningBrush");
            _ = CopyToClipboardAsync(selected);
            return;
        }
        Log.Information("开始拖出远程文件；数量 {Count}", selected.Length);
        try { RemoteFileDataObject.Start(Endpoint, selected); }
        catch (Exception ex) { Log.Error(ex, "发起远程文件拖拽异常"); }
    }
    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) { Log.Debug(ex, "检查管理员权限失败"); return false; }
    }
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        bool accept = client != null && !busy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        if (busy || client is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        var endpoint = Endpoint;
        string targetDirectory = directory;
        long total = paths.Sum(PathSize);
        long completedBytes = 0;
        var remoteEntries = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        await ExecuteAsync(async ct =>
        {
            BusyText.Text = paths.Length == 1 ? "正在上传拖入的内容…" : $"正在上传 {paths.Length} 项…";
            SetBusyProgress(0, "准备上传…");
            async Task<Dictionary<string, bool>> NamesAsync(string remoteDir)
            {
                if (remoteEntries.TryGetValue(remoteDir, out var cached)) return cached;
                var entries = (await endpoint.CallAsync(new("list", remoteDir), ct)).Entries ?? [];
                var names = entries.ToDictionary(e => e.Name, e => e.IsDirectory, StringComparer.OrdinalIgnoreCase);
                remoteEntries[remoteDir] = names;
                return names;
            }
            async Task UploadPathAsync(string local, string remoteDir)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(local))
                {
                    string name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string target = JoinRemote(remoteDir, name);
                    var names = await NamesAsync(remoteDir);
                    if (names.TryGetValue(name, out bool isDirectory))
                    {
                        if (!isDirectory) throw new IOException("同名文件已存在，无法创建文件夹：" + name);
                    }
                    else
                    {
                        await endpoint.CallAsync(new("mkdir", target), ct);
                        names[name] = true;
                    }
                    foreach (string child in Directory.EnumerateFileSystemEntries(local)) await UploadPathAsync(child, target);
                    return;
                }
                string fileName = Path.GetFileName(local);
                var listing = await NamesAsync(remoteDir);
                bool exists = listing.TryGetValue(fileName, out bool targetIsDirectory);
                if (exists && targetIsDirectory) throw new IOException("同名文件夹已存在，无法上传文件：" + fileName);
                if (exists && MessageBox.Show(Window.GetWindow(this), $"服务端已存在 {fileName}，是否覆盖？", "拖入上传", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                long length = new FileInfo(local).Length;
                BusyText.Text = $"正在上传 {fileName}…";
                var progress = new Progress<double>(value =>
                {
                    double overall = total == 0 ? 100 : (completedBytes + length * value / 100d) * 100d / total;
                    SetBusyProgress(overall, $"上传 {fileName} {overall:F0}%");
                });
                await endpoint.UploadAsync(local, JoinRemote(remoteDir, fileName), exists, progress, ct);
                completedBytes += length;
                listing[fileName] = false;
            }
            foreach (string path in paths) await UploadPathAsync(path, targetDirectory);
            unfilteredCache.Clear();
            await NavigateAsync(targetDirectory, false, ct);
            SetStatus($"已拖入 {paths.Length} 项", "SuccessBrush");
        }, cancellable: true);
    }
    private static string JoinRemote(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;
    private static long PathSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch (Exception ex) { Log.Debug(ex, "统计拖入文件大小失败；路径 {Path}", file); }
            }
            return total;
        }
        catch (Exception ex) { Log.Debug(ex, "统计拖入路径大小失败；路径 {Path}", path); return 0; }
    }
    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        string name = NewFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") throw new IOException("请输入有效的单级文件夹名称");
        await Endpoint.CallAsync(new("mkdir", directory.Length == 0 ? name : directory + "/" + name));
        unfilteredCache.Clear();
        NewFolderBox.Clear(); await NavigateAsync(directory);
        SetStatus("已新建文件夹", "SuccessBrush");
    });
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row) return;
        // 与资源管理器一致：右键已选中的行保留多选，右键未选中的行改为只选该行。
        if (!row.IsSelected)
        {
            FileGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
        row.Focus();
    }
    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGridRow { Item: RemoteEntry entry } row) return;
        row.ContextMenu = BuildMenu(entry);
        if (row.ContextMenu == null) e.Handled = true;
    }
    private ContextMenu BuildMenu(RemoteEntry entry)
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Click += async (_, _) =>
        {
            var selected = FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
            await CopyToClipboardAsync(selected.Contains(entry) ? selected : [entry]);
        };
        menu.Items.Add(copy);
        if (!entry.IsDirectory)
        {
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "删除服务端文件…" };
            delete.Click += async (_, _) =>
            {
                if (busy || client == null) return;
                Log.Information("请求确认删除服务端文件；路径 {RemotePath}", entry.Path);
                if (MessageBox.Show(Window.GetWindow(this), $"确定永久删除服务端文件？\n\n{entry.Path}\n\n此操作会删除服务端磁盘上的文件，无法通过本程序恢复。", "删除服务端文件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { Log.Information("用户取消删除服务端文件；路径 {RemotePath}", entry.Path); return; }
                await DeleteEntryAsync(entry);
            };
            menu.Items.Add(delete);
        }
        return menu;
    }
    private IProgress<RemoteTransferProgress> ArchiveProgress() => new Progress<RemoteTransferProgress>(value =>
    {
        bool indeterminate = value.Stage is "扫描目录" or "等待压缩";
        SetProgress(value.Percent, $"{value.Stage} {value.Percent:F1}%", indeterminate);
        SetStatus($"{value.Stage}：{value.Path}", "MutedBrush");
    });
    public Task AddArchiveAsync(RemoteEntry entry) => ExecuteAsync(async ct =>
    {
        BusyText.Text = "正在压缩…";
        SetProgress(0, "准备压缩");
        string archive = await Endpoint.CompressAsync(entry.Path, ArchiveProgress(), ct);
        // 刷新 ZIP 所在目录，根目录压缩的结果放在根目录内。
        string parent = ParentPath(entry.Path);
        unfilteredCache.Clear();
        await NavigateAsync(parent, parent.Length == 0, ct);
        SetProgress(100, "压缩完成");
        SetStatus("压缩完成：" + archive, "SuccessBrush");
    }, cancellable: true);
    // 界面菜单确认后调用；公开方法便于同一界面流程的自动化验证。
    public Task DeleteEntryAsync(RemoteEntry entry) => ExecuteAsync(async () =>
    {
        await Endpoint.DeleteFileAsync(entry);
        string parent = ParentPath(entry.Path);
        unfilteredCache.Clear();
        await NavigateAsync(parent, parent.Length == 0);
        SetStatus("已删除：" + entry.Path, "SuccessBrush");
    });
    public Task DownloadEntryAsync(RemoteEntry entry, string destination) => ExecuteAsync(async ct =>
    {        if (!entry.IsDirectory && !entry.IsZip) throw new IOException("请选择文件夹或 ZIP");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("本地目标已存在，请更换保存名称");
        BusyText.Text = "正在下载…";
        var endpoint = Endpoint; var progress = ArchiveProgress(); SetProgress(0, "准备下载");
        string archive = entry.IsDirectory ? await endpoint.CompressAsync(entry.Path, progress, ct) : entry.Path;
        await endpoint.DownloadZipAsync(archive, destination, progress, ct);
        if (entry.IsDirectory) { unfilteredCache.Clear(); await NavigateAsync(ParentPath(entry.Path), ParentPath(entry.Path).Length == 0, ct); }
        SetProgress(100, "下载完成");
        SetStatus("下载完成，SHA-256 校验通过", "SuccessBrush");
    }, cancellable: true);
    // 按用户和进程隔离，避免多个客户端实例互相删除正在使用的剪贴板会话。
    // 放在 LocalAppData\FileTransferDemo 下（日志和配置已验证可写），避免部分环境禁止写 %TEMP%。
    private static readonly string ClipboardTempRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileTransferDemo", "Clipboard", Environment.UserName, Environment.ProcessId.ToString());
    private static readonly List<string> clipboardSessions = [];
    // 复制到 Windows 剪贴板：先取到本机临时目录，再以文件拖放格式写入，资源管理器可直接粘贴。
    public Task CopyToClipboardAsync(RemoteEntry entry) => CopyToClipboardAsync([entry]);
    public Task CopyToClipboardAsync(IReadOnlyList<RemoteEntry> selected) => ExecuteAsync(async ct =>
    {
        if (selected.Count == 0) return;
        BusyText.Text = selected.Count == 1
            ? (selected[0].IsDirectory ? "正在准备复制文件夹…" : "正在准备复制文件…")
            : $"正在准备复制 {selected.Count} 项…";
        SetBusyProgress(0, BusyText.Text);
        string tempRoot = CreateClipboardSession();
        clipboardSessions.Add(tempRoot);
        var endpoint = Endpoint;
        int completedItems = 0;
        var progress = new Progress<RemoteTransferProgress>(value =>
        {
            double fraction = CopyStageFraction(value.Stage, value.Percent);
            double overall = (completedItems + fraction) * 100d / selected.Count;
            SetBusyProgress(overall, $"{value.Stage} {overall:F0}% · {completedItems + 1}/{selected.Count}：{Path.GetFileName(value.Path.TrimEnd('/'))}");
        });
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            string archive = await endpoint.CompressAsync(item.Path, progress, ct);
            string zipLocal = Path.Combine(tempRoot, "__copy-" + Guid.NewGuid().ToString("N") + ".zip");
            await endpoint.DownloadZipAsync(archive, zipLocal, progress, ct);
            ct.ThrowIfCancellationRequested();
            try { ZipFile.ExtractToDirectory(zipLocal, tempRoot, true); }
            catch (Exception ex) { throw new IOException("解压复制内容失败：" + ex.Message, ex); }
            File.Delete(zipLocal);
            await DeleteServerArchiveAsync(archive, ct);
            completedItems++;
            SetBusyProgress(completedItems * 100d / selected.Count, $"已完成 {completedItems}/{selected.Count}（{completedItems * 100d / selected.Count:F0}%）");
        }
        string[] paths = Directory.GetFileSystemEntries(tempRoot);
        if (paths.Length == 0) throw new IOException("复制内容为空");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        Clipboard.SetDataObject(data, true);
        SetBusyProgress(100, "复制完成");
        SetStatus(selected.Count == 1 ? $"已复制 {selected[0].Name}，可在本地文件夹粘贴" : $"已复制 {selected.Count} 项，可在本地文件夹粘贴", "SuccessBrush");
    }, cancellable: true);
    // 单个条目的压缩/下载阶段折算成整体进度，避免压缩完成后进度回退。
    private static double CopyStageFraction(string? stage, double percent)
    {
        double p = Math.Clamp(percent, 0, 100) / 100d;
        return stage switch
        {
            "扫描目录" => 0,
            "压缩" => 0.35 * p,
            "计算校验值" or "准备下载" or "接收数据" => 0.35,
            "下载" => 0.35 + 0.63 * p,
            "下载完成" => 0.99,
            _ => 0.35 * p
        };
    }
    private static string CreateClipboardSession()
    {
        try
        {
            Directory.CreateDirectory(ClipboardTempRoot);
            CleanClipboardTemp();
            CleanStaleClipboardRoots();
            string session = Path.Combine(ClipboardTempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            return session;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 默认目录不可用时依次尝试 LocalAppData 独立目录和系统临时目录。
            Log.Warning(ex, "默认剪贴板临时目录不可用，尝试备用目录");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string fallback in new[]
            {
                Path.Combine(local, "FileTransferDemo", "Clipboard-" + Guid.NewGuid().ToString("N")),
                Path.Combine(Path.GetTempPath(), "FileTransferClipboard-" + Guid.NewGuid().ToString("N"))
            })
            {
                try
                {
                    Directory.CreateDirectory(fallback);
                    return fallback;
                }
                catch (Exception retry) when (retry is UnauthorizedAccessException or IOException)
                {
                    Log.Warning(retry, "剪贴板备用目录不可用；路径 {Path}", fallback);
                }
            }
            throw new IOException("无法创建剪贴板临时目录，请检查 LocalAppData 和 TEMP 目录权限", ex);
        }
    }
    private static void CleanClipboardTemp()
    {
        foreach (string session in clipboardSessions.ToArray())
        {
            try { DeleteDirectory(session); }
            catch (Exception ex) { Log.Debug(ex, "清理剪贴板会话失败；路径 {Path}", session); }
            clipboardSessions.Remove(session);
        }
        try
        {
            if (!Directory.Exists(ClipboardTempRoot)) return;
            foreach (string directory in Directory.GetDirectories(ClipboardTempRoot))
            {
                if (clipboardSessions.Contains(directory)) continue;
                try { DeleteDirectory(directory); }
                catch (Exception ex) { Log.Debug(ex, "清理旧剪贴板临时目录失败；路径 {Path}", directory); }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "扫描剪贴板临时目录失败"); }
    }
    private static void CleanStaleClipboardRoots()
    {
        try
        {
            string userRoot = Path.GetDirectoryName(ClipboardTempRoot)!;
            if (!Directory.Exists(userRoot)) return;
            foreach (string directory in Directory.GetDirectories(userRoot))
            {
                if (string.Equals(directory, ClipboardTempRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // 只清理长期未使用的其他进程目录，避免误删正在粘贴的数据。
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(7)) continue;
                    DeleteDirectory(directory);
                }
                catch (Exception ex) { Log.Debug(ex, "清理过期剪贴板目录失败；路径 {Path}", directory); }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "扫描过期剪贴板目录失败"); }
    }
    private static void DeleteDirectory(string path)
    {        if (!Directory.Exists(path)) return;
        // 解压出来的文件可能带只读属性，先清除再删除。
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (Exception ex) { Log.Debug(ex, "清除只读属性失败；路径 {Path}", file); }
        }
        Directory.Delete(path, true);
    }
    private async Task DeleteServerArchiveAsync(string archive, CancellationToken ct)
    {
        try
        {
            string parent = ParentPath(archive);
            var listing = await Endpoint.CallAsync(new("list", parent), ct);
            var zip = (listing.Entries ?? []).FirstOrDefault(e => !e.IsDirectory && e.Path == archive);
            if (zip is not null) await Endpoint.DeleteFileAsync(zip, ct);
        }
        catch (Exception ex) { Log.Warning(ex, "清理复制用的服务端 ZIP 失败；路径 {ArchivePath}", archive); }
    }
    private Task ExecuteAsync(Func<Task> action) => ExecuteAsync(_ => action(), false);
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, bool cancellable = false)
    {
        if (busy) return;
        busy = true;
        FileGrid.IsEnabled = false; FolderTree.IsEnabled = false; FilterHost.IsEnabled = false; NavHost.IsEnabled = false;
        ShowBusy("正在处理…");
        using var source = cancellable ? new CancellationTokenSource() : null;
        operationCancel = source;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        try { await action(source?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
        {
            Log.Information("用户取消远程浏览器操作；当前目录 {RemoteDirectory}", directory);
            SetStatus("已取消", "MutedBrush");
        }
        catch (Exception ex) { Log.Error(ex, "远程浏览器操作失败；当前目录 {RemoteDirectory}", directory); SetStatus(ex.Message, "DangerBrush"); }
        finally
        {
            UploadProgress.IsIndeterminate = false;
            HideProgress();
            HideBusy();
            CancelButton.Visibility = Visibility.Collapsed;
            operationCancel = null;
            busy = false;
            FileGrid.IsEnabled = true; FolderTree.IsEnabled = true; FilterHost.IsEnabled = true; NavHost.IsEnabled = true;
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (operationCancel is null) return;
        CancelButton.IsEnabled = false;
        SetStatus("正在取消…", "WarningBrush");
        operationCancel.Cancel();
    }
    private void SetStatus(string text, string brushKey = "MutedBrush")
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        BrowserStatusDot.Fill = ThemeBrush(brushKey);
    }
    private void ShowBusy(string text)
    {
        BusyProgress.IsIndeterminate = true;
        BusyProgress.Value = 0;
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
    }
    private void SetBusyProgress(double percent, string text)
    {
        BusyProgress.IsIndeterminate = false;
        BusyProgress.Value = Math.Clamp(percent, 0, 100);
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
    }
    private void HideBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        BusyProgress.IsIndeterminate = true;
        BusyProgress.Value = 0;
    }
    private void ShowProgress(string text)
    {
        UploadText.Text = text;
        UploadText.ToolTip = text;
        ProgressPanel.Visibility = Visibility.Visible;
    }
    private void SetProgress(double value, string text, bool indeterminate = false)
    {
        UploadProgress.IsIndeterminate = indeterminate;
        if (!indeterminate) UploadProgress.Value = value;
        UploadText.Text = text;
        UploadText.ToolTip = text;
        ProgressPanel.Visibility = Visibility.Visible;
    }
    private void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        UploadText.Text = "";
    }
    private Brush ThemeBrush(string key) => (Brush)FindResource(key);
}

public sealed class FileIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RemoteEntry entry) return null;
        string key = entry.IsDirectory ? "<folder>" : Path.GetExtension(entry.Name);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        string path = entry.IsDirectory ? "folder" : "file" + key;
        const uint useAttributes = 0x10, icon = 0x100, smallIcon = 0x1;
        if (SHGetFileInfo(path, entry.IsDirectory ? 0x10u : 0x80u, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), useAttributes | icon | smallIcon) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); Cache[key] = image; return image;
        }
        finally { DestroyIcon(info.Icon); }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileKindConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RemoteEntry entry) return "";
        if (entry.IsDirectory) return "文件夹";
        return entry.Kind switch
        {
            ".txt" => "文本",
            ".log" => "日志",
            ".json" => "JSON",
            ".jpg" or ".png" => "图片",
            ".exe" => "程序",
            ".zip" => "ZIP",
            ".dll" => "DLL",
            _ => string.IsNullOrEmpty(entry.Kind) ? "文件" : entry.Kind.TrimStart('.').ToUpperInvariant()
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RemoteEntry entry || entry.IsDirectory) return "";
        double bytes = entry.Length;
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024d * 1024) return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024d * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / (1024d * 1024 * 1024):N1} GB";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
