using System.IO;
using System.IO.Compression;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private int connectionVersion;
    private readonly DirectoryNavigationState navigation = new();
    private string directory => navigation.Path;
    private string? fullDirectoryPath;
    private bool busy;
    private ImageViewer? annotationWindow;
    private Dictionary<string, RemoteAnnotation>? annotationIndex;
    private string? annotationReadError;
    private bool rebuilding;
    private bool suppressTreeSelected;
    // 逐级展开并选中时，Expanded 处理器不能再拉一次子项，否则会把刚选中的节点替换掉。
    private bool syncingTree;
    private System.Collections.ObjectModel.ObservableCollection<RemoteEntry> entries => navigation.Entries;
    private int totalCount => navigation.Total;
    private bool allLoaded => navigation.AllLoaded;
    private Task pageLoad => navigation.PageLoad;
    private readonly HashSet<TreeViewItem> loadingTreeNodes = [];
    private int navigationVersion => navigation.ListingVersion;
    private CancellationTokenSource? operationCancel;
    private static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(150);
    private const int LargeTextThreshold = 1024 * 1024;
    private readonly DispatcherTimer busyIndicatorTimer = new(DispatcherPriority.Normal) { Interval = BusyIndicatorDelay };
    private Window? hostWindow;
    private int pendingUp;
    private bool navigatingUp;
    private string? listFilter;
    private readonly NgProgressViewModel ngProgress = new();
    private string? ngOverviewPath => ngProgress.Path;
    private bool ngOverviewHasResult => ngProgress.HasResult;
    private Func<string, string?>? localEmptyText = null;
    private ScrollViewer? fileScroll;
    private bool scrollHooked;
    private bool scrollResetPending;
    private Point dragOrigin;
    private bool dragArmed;
    private RemoteEntry[]? dragSelection;
    private bool dragInProgress;
    public event Action<string, string>? StatusChanged;
    public event Action<string, double, bool, bool>? ProgressChanged;
    public event Action<int, int?>? SelectionCountChanged;
    public bool IsBusy => busy || dragInProgress;
    public string? ProtocolWarning { get; private set; }
    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(FileGrid.ItemsSource) is not ListCollectionView view) return;
        int version = navigationVersion;
        var listing = entries;
        e.Handled = true;
        string member = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(member)) { view.CustomSort = null; return; }
        // 分页状态下先取回全部条目，避免只对当前页排序造成顺序错乱。
        if (!allLoaded)
        {
            SetStatus("正在加载全部条目…", "MutedBrush");
            if (!await LoadAllPagesAsync()) return;
        }
        if (version != navigationVersion || !ReferenceEquals(listing, entries)) return;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        view.CustomSort = new RemoteEntryComparer(member, direction);
        e.Column.SortDirection = direction;
    }
    public RemoteBrowser()
    {
        InitializeComponent();
        navigation.PageReceived += ApplyAnnotationStatuses;
        busyIndicatorTimer.Tick += (_, _) => DisplayBusyIndicator();
        ((ContextMenu)FindResource("ColumnSettingsMenu")).Opened += ColumnSettings_Opened;
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        var style = new Style(typeof(DataGridRow), FileGrid.RowStyle);
        style.Setters.Add(new EventSetter(ContextMenuOpeningEvent, new ContextMenuEventHandler(Row_ContextMenuOpening)));
        style.Setters.Add(new EventSetter(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(Row_RightClick)));
        // 占位菜单确保 WPF 会引发 ContextMenuOpening；打开时按当前行生成真实菜单。
        style.Setters.Add(new Setter(ContextMenuProperty, new ContextMenu()));
        FileGrid.RowStyle = style;
        UpdateEmptyState();
        BuildLocalFilters(FilterHost, FilterRightHost);
        ShowDisconnectedPath();
        FileGrid.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Grid_MouseLeftButtonDown), true);
        FileGrid.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(Grid_MouseMove), true);
        FileGrid.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Grid_MouseLeftButtonUp), true);
        FileGrid.SelectionChanged += (_, _) =>
        {
            UpdateItemCount();
            UpdateAnnotateButton();
        };
        UpdateAnnotateButton();
        Loaded += Browser_Loaded;
        Unloaded += Browser_Unloaded;
    }
    public async Task ConnectAsync(RemoteFileClient endpoint)
    {
        if (busy) throw new IOException("当前操作尚未结束，请稍后重新连接");
        int connection = ++connectionVersion;
        client = endpoint;
        navigation.SetClient(new RemoteDirectoryClient(endpoint));
        ProtocolWarning = null;
        navigation.InvalidateCache();
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        try
        {
            var hello = await endpoint.CallAsync(new("hello"));
            if (connection != connectionVersion) return;
            if (hello.Protocol > 0 && hello.Protocol != RemoteProtocol.Version)
                ProtocolWarning = $"客户端协议 v{RemoteProtocol.Version}，服务端协议 v{hello.Protocol}，请更新两端";
        }
        catch (Exception ex)
        {
            if (connection != connectionVersion) return;
            ProtocolWarning = "服务端不支持版本握手，可能为旧版本，请更新服务端";
            Log.Warning(ex, "远程文件服务端版本握手失败");
        }
        await NavigateAsync("", true);
    }
    public void Disconnect()
    {
        connectionVersion++;
        dragArmed = false;
        dragSelection = null;
        operationCancel?.Cancel();
        client = null;
        ProtocolWarning = null;
        navigation.SetClient(null);
        scrollResetPending = false;
        navigation.InvalidateCache();
        FolderTree.Items.Clear();
        FileGrid.ItemsSource = null;
        annotationIndex = null;
        annotationReadError = null;
        FileGrid.SelectionMode = DataGridSelectionMode.Extended;
        ShowDisconnectedPath();
        UpdateItemCount();
        HideBusy();
        HideProgress();
        SetStatus("尚未连接", "MutedBrush");
        UpdateEmptyState();
        UpdateAnnotateButton();
    }
    private RemoteFileClient Endpoint => client ?? throw new IOException("请先连接服务端");
    private async Task NavigateAsync(string path, bool rebuild = false, CancellationToken ct = default, bool revealReturnedFolder = false)
    {
        var navigationTime = Stopwatch.StartNew();
        bool ownedBusy = !busy;
        string progress = "正在打开 " + (path.Split('/').LastOrDefault(part => part.Length > 0) ?? "共享目录") + "…";
        if (ownedBusy) ShowBusy(progress);
        else { BusyText.Text = progress; SetStatus(progress); }
        int? appliedVersion = null;
        try
        {
            var endpoint = Endpoint;
            string previous = directory;
            var response = await navigation.NavigateAsync(path, listFilter, ct);
            if (response is null || !ReferenceEquals(client, endpoint)) return;
            int version = navigationVersion;
            appliedVersion = version;
            Log.Debug("目录列表已返回；路径 {RemoteDirectory}，耗时 {ElapsedMs} ms", path, navigationTime.ElapsedMilliseconds);
            if (!string.Equals(previous, path, StringComparison.Ordinal))
            {
                foreach (var column in FileGrid.Columns) column.SortDirection = null;
                FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
            }
            var nameDirection = FileGrid.Columns[0].SortDirection;
            scrollResetPending = false;
            annotationIndex = null;
            annotationReadError = null;
            FileGrid.SelectionMode = IsDateDirectory(path) ? DataGridSelectionMode.Single : DataGridSelectionMode.Extended;
            FileGrid.ItemsSource = entries;
            if (nameDirection is { } direction && CollectionViewSource.GetDefaultView(entries) is ListCollectionView view)
            {
                view.CustomSort = new RemoteEntryComparer("Name", direction);
                FileGrid.Columns[0].SortDirection = direction;
            }
            // 返回上一级时定位到刚才所在的文件夹；其他目录切换回到顶部，同目录刷新保留位置。
            bool movedUp = revealReturnedFolder && !string.IsNullOrEmpty(previous) && string.Equals(ParentPath(previous), path, StringComparison.Ordinal);
            bool resetScroll = !string.Equals(previous, path, StringComparison.Ordinal);
            if (movedUp)
            {
                var anchor = entries.FirstOrDefault(e => e.Path == previous && e.IsDirectory);
                while (anchor is null && !allLoaded)
                {
                    int before = entries.Count;
                    await LoadMoreAsync(ct);
                    if (version != navigationVersion) return;
                    if (entries.Count == before) break;
                    anchor = entries.FirstOrDefault(e => e.Path == previous && e.IsDirectory);
                }
                if (anchor is not null)
                {
                    SelectAndReveal(anchor);
                    resetScroll = false;
                }
            }
            UpdatePath(response);
            UpdateItemCount();
            SetStatus(response.Warning ?? "正在更新目录…", response.Warning is null ? "MutedBrush" : "WarningBrush");
            UpdateEmptyState();
            Log.Debug("目录首屏已绑定；路径 {RemoteDirectory}，累计耗时 {ElapsedMs} ms", path, navigationTime.ElapsedMilliseconds);
            if (rebuild || FolderTree.Items.Count == 0)
            {
                rebuilding = true;
                try
                {
                    FolderTree.Items.Clear();
                    var root = MakeNode(new(response.RootName ?? "共享根目录", "", true, 0, default, default));
                    var children = await UnfilteredEntriesAsync(path, ct);
                    if (version != navigationVersion) return;
                    SetChildren(root, children);
                    root.IsExpanded = true;
                    FolderTree.Items.Add(root);
                }
                finally { rebuilding = false; }
            }
            else if (FindNode(FolderTree.Items, path) is TreeViewItem { IsExpanded: true } node)
            {
                // 目录树始终展示未过滤的完整内容，过滤只作用于右侧列表；折叠的节点不预加载。
                var children = await UnfilteredEntriesAsync(path, ct);
                if (version != navigationVersion) return;
                SetChildren(node, children);
            }
            // 双击文件夹、点面包屑、退格或“上一级”切换目录后，把树的选中项同步到当前目录。
            BusyText.Text = "正在同步目录位置…";
            await SyncTreeSelectionAsync(path);
            if (version != navigationVersion) return;
            BusyText.Text = "正在读取标注状态…";
            await RefreshAnnotationStatusAsync(path, ct);
            if (version != navigationVersion) return;
            BusyText.Text = "正在读取 NG 进度…";
            await UpdateNgOverviewAsync(path, ct);
            if (version != navigationVersion) return;
            if (resetScroll) await ResetFileScrollAsync();
            if (response.Warning is null && StatusText.Text == "正在更新目录…")
                SetStatus(entries.Count == 0 ? EmptyTextFor(directory) : "就绪", "MutedBrush");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
            && (client is null || appliedVersion is int applied && applied != navigationVersion)) { }
        finally
        {
            if (ownedBusy) HideBusy();
            Log.Debug("目录导航收尾完成；路径 {RemoteDirectory}，总耗时 {ElapsedMs} ms", path, navigationTime.ElapsedMilliseconds);
        }
    }
    private void UpdateItemCount()
    {
        int? count = client is not null && FileGrid.Visibility == Visibility.Visible
            ? Math.Max(totalCount, entries.Count) : null;
        SelectionCountChanged?.Invoke(FileGrid.SelectedItems.Count, count);
    }
    // 目录树始终使用未过滤结果，且不受分页影响；结果短时间缓存，避免来回切换时重复请求。
    private Task<RemoteEntry[]> UnfilteredEntriesAsync(string path, CancellationToken ct) => navigation.GetUnfilteredAsync(path, ct);
    private async Task LoadMoreAsync(CancellationToken ct = default)
    {
        int version = navigationVersion;
        try
        {
            await navigation.LoadMoreAsync(ct);
            if (version != navigationVersion) return;
            UpdateItemCount();
            UpdateEmptyState();
            if (CollectionViewSource.GetDefaultView(entries) is ListCollectionView view && view.CustomSort is not null) view.Refresh();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != navigationVersion) return;
            Log.Warning(ex, "加载更多条目失败；目录 {RemoteDirectory}", directory);
            SetStatus("加载更多失败：" + ex.Message, "DangerBrush");
        }
    }
    private async Task<bool> LoadAllPagesAsync()
    {
        int version = navigationVersion;
        var listing = entries;
        while (!allLoaded && client is not null && version == navigationVersion && ReferenceEquals(listing, entries))
        {
            int before = entries.Count;
            await LoadMoreAsync();
            if (entries.Count == before) break;
        }
        return version == navigationVersion && ReferenceEquals(listing, entries) && allLoaded;
    }
    private void FileScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (scrollResetPending || busy || !pageLoad.IsCompleted || allLoaded) return;
        // 更换列表会改变滚动范围并压缩旧偏移量；这种布局事件不能当成向下滚动。
        if (!ReferenceEquals(e.OriginalSource, sender) || e.VerticalChange <= 0 || e.ExtentHeightChange != 0) return;
        double prefetch = Math.Min(200, e.ViewportHeight);
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - prefetch) return;
        _ = LoadMoreAsync();
    }
    private void ShowDisconnectedPath()
    {
        ngProgress.Reset();
        ClearNgOverviewResult("");
        SetNgOverviewVisible(false);
        Breadcrumb.Children.Clear();
        Breadcrumb.Children.Add(new TextBlock
        {
            Text = "尚未连接",
            FontSize = 12,
            Foreground = ThemeBrush("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Breadcrumb.ToolTip = null;
        fullDirectoryPath = null;
        CopyPathButton.IsEnabled = false;
        BreadcrumbScroll.ScrollToLeftEnd();
    }
    private void UpdatePath(RemoteResponse response)
    {
        string rootName = response.RootName ?? "共享根目录";
        Breadcrumb.Children.Clear();
        AddBreadcrumb(rootName, "", directory.Length == 0);
        if (directory.Length > 0)
        {
            string path = "";
            string[] parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                path = path.Length == 0 ? parts[i] : path + "/" + parts[i];
                AddBreadcrumb(parts[i], path, i == parts.Length - 1);
            }
        }
        string host = Endpoint.Host.Contains(':') ? $"[{Endpoint.Host.Trim('[', ']')}]" : Endpoint.Host;
        fullDirectoryPath = $"{host}:{Endpoint.Port}/{rootName}" + (directory.Length == 0 ? "" : "/" + directory);
        Breadcrumb.ToolTip = fullDirectoryPath;
        CopyPathButton.IsEnabled = true;
        // 出现横向滚动条时默认停在最左，当前目录会被藏起来；等布局完成再滚到最右。
        Dispatcher.BeginInvoke(new Action(() => BreadcrumbScroll.ScrollToRightEnd()), DispatcherPriority.Loaded);
    }
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (fullDirectoryPath is null) return;
        try
        {
            Clipboard.SetText(fullDirectoryPath);
            SetStatus("已复制完整路径", "SuccessBrush");
        }
        catch (ExternalException ex)
        {
            Log.Warning(ex, "复制完整路径失败");
            SetStatus("无法访问剪贴板，请重试", "WarningBrush");
        }
    }

    private void ColumnSettings_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        foreach (var column in new[] { CreatedTimeColumn, ModifiedTimeColumn })
        {
            var item = new MenuItem
            {
                Header = column.Header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true
            };
            item.Click += (_, _) => column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            menu.Items.Add(item);
        }
    }
    private void AddBreadcrumb(string label, string path, bool current)
    {
        if (Breadcrumb.Children.Count > 0)
        {
            Breadcrumb.Children.Add(new TextBlock
            {
                Text = "›",
                FontSize = 15,
                Margin = new Thickness(2, 0, 2, 0),
                Foreground = ThemeBrush("SubtleBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 },
            Style = TryFindResource("GhostButton") as Style,
            Padding = new Thickness(5, 0, 5, 0),
            MinHeight = 24,
            FontSize = 12,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            ToolTip = path.Length == 0 ? "共享根目录" : path
        };
        if (current) button.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        button.Click += async (_, _) => await ExecuteAsync(() => NavigateAsync(path, path.Length == 0));
        Breadcrumb.Children.Add(button);
    }
    private void UpdateEmptyState()
    {
        bool connected = client != null;
        EmptyState.Visibility = connected && entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = connected ? EmptyTextFor(directory) : "连接后可浏览共享目录";
        EmptyUploadButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }
    private string EmptyTextFor(string path) => localEmptyText?.Invoke(path) ?? "此文件夹为空";
    private void SelectAndReveal(RemoteEntry entry)
    {
        // 列表刚换过 ItemsSource，先强制布局再选中，否则 SelectedItem 赋值会被忽略。
        int version = navigationVersion;
        FileGrid.UpdateLayout();
        FileGrid.SelectedItem = entry;
        FileGrid.ScrollIntoView(entry);
        // 等新列表完成布局后再定位一次；若期间又切换了目录，这次延迟定位必须作废，
        // 否则会覆盖后续选择（例如右键其它行）。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (version != navigationVersion || !entries.Contains(entry)) return;
            FileGrid.SelectedItem = entry;
            FileGrid.ScrollIntoView(entry);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private Task ResetFileScrollAsync()
    {
        fileScroll ??= FindVisualChild<ScrollViewer>(FileGrid);
        if (fileScroll is null) return Task.CompletedTask;
        int version = navigationVersion;
        scrollResetPending = true;
        // 滚动定位是界面收尾，不应让整个导航等待 ContextIdle 才解除操作锁。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (version != navigationVersion || fileScroll is not { } scroll) return;
                if (FileGrid.IsVisible && FileGrid.Items.Count > 0)
                    FileGrid.ScrollIntoView(FileGrid.Items[0]);
                scroll.ScrollToVerticalOffset(0);
                if (FileGrid.IsVisible) FileGrid.UpdateLayout();
            }
            catch (Exception ex) { Log.Debug(ex, "目录滚动复位失败；目录 {RemoteDirectory}", directory); }
            finally { if (version == navigationVersion) scrollResetPending = false; }
        }), DispatcherPriority.Loaded);
        return Task.CompletedTask;
    }
    private static T? FindVisualChild<T>(DependencyObject parent, Predicate<T>? predicate = null) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && (predicate is null || predicate(match))) return match;
            if (FindVisualChild<T>(child, predicate) is { } nested) return nested;
        }
        return null;
    }
    // 由本地个性化扩展实现；未实现时界面不显示额外过滤控件。
    // host = 工具栏左侧的动作区，filters = 右侧紧邻标注按钮的过滤区。
    partial void BuildLocalFilters(StackPanel host, StackPanel filters);
    // 过滤值变化后重新加载右侧列表；目录树不受影响。
    private async void SetLocalFilter(string? value)
    {
        if (string.Equals(listFilter, value, StringComparison.Ordinal)) return;
        listFilter = value;
        if (client is null) return;
        await ExecuteAsync(async () =>
        {
            await NavigateAsync(directory);
            // 同目录的过滤条件变化也需要回到顶部。
            await ResetFileScrollAsync();
        });
    }
    // 逐级展开并加载，直到目标目录出现在树里，再把高亮移到它上面。
    // 只展开到目标目录本身，不预加载它下面的子目录，避免一次导航拉取整棵树。
    private async Task SyncTreeSelectionAsync(string path)
    {
        if (rebuilding) return;
        int version = navigationVersion;
        var node = FindNode(FolderTree.Items, path);
        if (node is null && path.Length > 0)
        {
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string walked = "";
            TreeViewItem? parent = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault();
            syncingTree = true;
            try
            {
                for (int i = 0; i < parts.Length && parent is not null; i++)
                {
                    string child = walked.Length == 0 ? parts[i] : walked + "/" + parts[i];
                    if (!parent.IsExpanded)
                    {
                        // 展开会触发 Expanded 处理器，但 syncingTree 为真，它不会重复拉取；
                        // 这里直接填充子项，保证紧接着就能找到下一级节点。注意加载的是父目录的内容。
                        parent.IsExpanded = true;
                        var children = await UnfilteredEntriesAsync(walked, CancellationToken.None);
                        if (version != navigationVersion) return;
                        SetChildren(parent, children);
                    }
                    node = FindNode(parent.Items, child);
                    walked = child;
                    parent = node;
                }
            }
            finally { syncingTree = false; }
        }
        if (node is null) return;
        suppressTreeSelected = true;
        try
        {
            node.IsSelected = true;
            node.BringIntoView();
        }
        finally { suppressTreeSelected = false; }
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
        // 菜单按需创建，避免每次加载数百节点时在 UI 线程预建所有菜单。
        node.ContextMenu = new ContextMenu();
        node.ContextMenuOpening += (_, e) =>
        {
            var target = e.OriginalSource as TreeViewItem ?? ItemsControl.ContainerFromElement(null, e.OriginalSource as DependencyObject);
            if (!ReferenceEquals(target, node)) return;
            node.ContextMenu = BuildMenu(entry);
            if (node.ContextMenu is null) e.Handled = true;
        };
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
                if (!ReferenceEquals(e.OriginalSource, node) || rebuilding || syncingTree) return;
                if (node.Items.Count != 1 || node.Items[0] is not string || !loadingTreeNodes.Add(node)) return;
                var endpoint = client;
                string progress = "正在展开 " + entry.Name + "…";
                node.Items[0] = "正在读取目录…";
                if (!IsBusy) SetStatus(progress);
                var elapsed = Stopwatch.StartNew();
                try
                {
                    var children = await UnfilteredEntriesAsync(entry.Path, CancellationToken.None);
                    if (ReferenceEquals(client, endpoint) && ReferenceEquals(FindNode(FolderTree.Items, entry.Path), node))
                        SetChildren(node, children);
                }
                catch (OperationCanceledException)
                {
                    if (ReferenceEquals(client, endpoint) && ReferenceEquals(FindNode(FolderTree.Items, entry.Path), node))
                    {
                        node.Items.Clear(); node.Items.Add("目录已更新，折叠后重试");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "展开目录失败；路径 {RemoteDirectory}", entry.Path);
                    if (ReferenceEquals(client, endpoint) && ReferenceEquals(FindNode(FolderTree.Items, entry.Path), node))
                    {
                        node.Items.Clear(); node.Items.Add("加载失败，折叠后重试");
                        if (!IsBusy) SetStatus("展开目录失败：" + ex.Message, "WarningBrush");
                    }
                }
                finally
                {
                    loadingTreeNodes.Remove(node);
                    if (StatusText.Text == progress) SetStatus("就绪");
                    Log.Debug("目录树展开结束；路径 {RemoteDirectory}，耗时 {ElapsedMs} ms", entry.Path, elapsed.ElapsedMilliseconds);
                }
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
        var existing = node.Items.OfType<TreeViewItem>().Where(item => item.Tag is RemoteEntry)
            .ToDictionary(item => ((RemoteEntry)item.Tag).Path, StringComparer.Ordinal);
        var next = children.Select(child => existing.TryGetValue(child.Path, out var item) && Equals(item.Tag, child) ? item : MakeNode(child)).ToArray();
        if (next.SequenceEqual(node.Items.Cast<object>())) return;
        node.Items.Clear();
        foreach (var child in next) node.Items.Add(child);
    }
    private async void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // 代码同步高亮时不能再触发一次导航，否则树和列表会来回跳。
        if (rebuilding || suppressTreeSelected) return;
        if (e.NewValue is not TreeViewItem { Tag: RemoteEntry entry } || !entry.IsDirectory) return;
        if (string.Equals(entry.Path, directory, StringComparison.Ordinal)) return;
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
        if (entry.IsDirectory) { await NavigateAsync(entry.Path); return; }
        bool ownsIndicator = !busy;
        if (ownsIndicator) ShowBusy("正在打开文件…");
        else BusyText.Text = "正在打开文件…";
        try
        {
            var endpoint = Endpoint;
            Window viewer;
            if (entry.IsText)
            {
                var elapsed = Stopwatch.StartNew();
                BusyText.Text = "正在读取文本…";
                var response = await Task.Run(() => endpoint.CallAsync(new("text", entry.Path)));
                if (!ReferenceEquals(client, endpoint)) return;
                // 大文本赋值和首屏布局必须在 UI 线程上完成，先让等待提示获得一次绘制机会。
                if (entry.Length >= LargeTextThreshold || response.Text?.Length >= LargeTextThreshold
                    || elapsed.Elapsed >= BusyIndicatorDelay)
                {
                    BusyText.Text = "正在准备文本查看器…";
                    DisplayBusyIndicator();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (!ReferenceEquals(client, endpoint)) return;
                }
                viewer = new TextViewer(endpoint, entry.Path, response);
            }
            else if (entry.IsImage)
            {
                string parent = ParentPath(entry.Path);
                var siblings = (await endpoint.CallAsync(new("list", parent))).Entries ?? [];
                var imageViewer = new ImageViewer(endpoint, siblings.Where(e => e.IsImage).OrderBy(e => e.CreatedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), entry.Path);
                imageViewer.Closed += async (_, _) =>
                {
                    try { await RestoreViewedImageAsync(endpoint, parent, imageViewer.CurrentPath); }
                    catch (Exception ex) { Log.Debug(ex, "关闭查看器后定位图片失败；路径 {RemotePath}", imageViewer.CurrentPath); }
                };
                viewer = imageViewer;
            }
            else if (entry.IsExe) viewer = new ProcessViewer(endpoint, entry.Path);
            else { SetStatus("此文件类型暂不提供预览或编辑：" + entry.Kind, "MutedBrush"); return; }
            viewer.Owner = Window.GetWindow(this); viewer.Show();
            SetStatus("已打开 " + entry.Name, "MutedBrush");
        }
        finally { if (ownsIndicator) HideBusy(); }
    }
    private async Task RestoreViewedImageAsync(RemoteFileClient endpoint, string parent, string imagePath)
    {
        if (!IsLoaded || !ReferenceEquals(client, endpoint) || !string.Equals(directory, parent, StringComparison.Ordinal)) return;

        int version = navigationVersion;
        var entry = entries.FirstOrDefault(item => item.Path == imagePath);
        while (entry is null && !allLoaded)
        {
            int before = entries.Count;
            await LoadMoreAsync();
            if (!IsLoaded || version != navigationVersion || !ReferenceEquals(client, endpoint) || entries.Count == before) return;
            entry = entries.FirstOrDefault(item => item.Path == imagePath);
        }
        if (entry is not null) SelectAndReveal(entry);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        // 刷新会丢弃未过滤缓存，确保目录树看到服务端最新内容。
        navigation.InvalidateCache();
        // 根目录重建可移除树中的过期节点；目录内刷新只更新当前节点。
        await NavigateAsync(directory, directory.Length == 0);
    });
    private async void Parent_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => NavigateAsync(ParentPath(directory), revealReturnedFolder: true));
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
        HideBusy();
        if (fileScroll is not null) { fileScroll.ScrollChanged -= FileScroll_ScrollChanged; fileScroll = null; }
        scrollHooked = false;
        if (hostWindow is null) return;
        hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        hostWindow = null;
    }
    private async void HostWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (dragInProgress) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.A or Key.C)
        {
            // 输入框里的 Ctrl+A/C 继续用于编辑文字。
            if (!FileGrid.IsVisible || Keyboard.FocusedElement is TextBox) return;
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
        // 窗口级的 PreviewKeyDown 已经处理过（隧道先到窗口），这里不能再执行一次，
        // 否则退格会连退两级。
        if (e.Handled) return;
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
    // NG 过滤是否开启（本地个性化过滤器把 listFilter 设为 "ng"）。
    private bool NgFilterEnabled => string.Equals(listFilter, "ng", StringComparison.OrdinalIgnoreCase);
    private bool CanOpenAnnotation => !IsBusy && client is not null && annotationWindow is null;

    private async Task UpdateNgOverviewAsync(string path, CancellationToken ct)
    {
        string equipment = path.Split('/').Last();
        if (equipment.Equals("SaveImages", StringComparison.OrdinalIgnoreCase))
            equipment = ParentPath(path).Split('/').Last();
        if (!NgFilterEnabled || !equipment.StartsWith("A6", StringComparison.OrdinalIgnoreCase))
        {
            ngProgress.Reset();
            ClearNgOverviewResult("");
            SetNgOverviewVisible(false);
            return;
        }

        if (!string.Equals(ngOverviewPath, path, StringComparison.Ordinal))
        {
            ngProgress.SetPath(path);
            ClearNgOverviewResult("");
            if (NgEndDate.Text.Length == 0) NgEndDate.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (NgStartDate.Text.Length == 0) NgStartDate.Text = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            SetNgOverviewVisible(true);
        }
        if (NgOverviewPanel.Visibility == Visibility.Visible) await RefreshNgOverviewAsync(ct);
    }

    private void SetNgOverviewVisible(bool visible)
    {
        NgOverviewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var fileVisibility = visible ? Visibility.Collapsed : Visibility.Visible;
        FileGrid.Visibility = fileVisibility;
        AnnotateButton.Visibility = fileVisibility;
        NgOverviewOpenButton.Visibility = !visible && ngOverviewPath is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateItemCount();
    }

    private async void NgOverviewOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ngOverviewPath is null || busy) return;
        SetNgOverviewVisible(true);
        await ExecuteAsync(ct => RefreshNgOverviewAsync(ct), cancellable: true);
    }

    private void NgOverviewReturn_Click(object sender, RoutedEventArgs e) => SetNgOverviewVisible(false);

    private async void NgOverviewQuery_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(ct => RefreshNgOverviewAsync(ct), cancellable: true);

    private async void NgOverviewToday_Click(object sender, RoutedEventArgs e) => await QueryNgOverviewPresetAsync(0);

    private async void NgOverviewLastWeek_Click(object sender, RoutedEventArgs e) => await QueryNgOverviewPresetAsync(6);

    private Task QueryNgOverviewPresetAsync(int daysBack) => ExecuteAsync(async ct =>
    {
        var range = NgProgressViewModel.GetDatePreset(daysBack, DateTime.Today);
        NgStartDate.Text = range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        NgEndDate.Text = range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await RefreshNgOverviewAsync(ct);
    }, cancellable: true);

    private async void NgOverviewDate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ExecuteAsync(ct => RefreshNgOverviewAsync(ct), cancellable: true);
    }

    private void NgOverviewFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (ngOverviewHasResult && NgOverviewPanel.Visibility == Visibility.Visible) RenderNgOverview(keepScroll: false);
    }

    private void NgOverviewDate_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateNgDatePresets();
        if (NgOverviewPanel?.Visibility == Visibility.Visible)
            ClearNgOverviewResult("日期已更改，请点击查询");
    }

    private void UpdateNgDatePresets()
    {
        if (NgTodayButton is null || NgLastWeekButton is null) return;
        bool validStart = NgProgressViewModel.TryDate(NgStartDate.Text, out var start);
        bool validEnd = NgProgressViewModel.TryDate(NgEndDate.Text, out var end);
        bool valid = validStart && validEnd;
        var today = DateTime.Today;
        System.Windows.Controls.Primitives.Selector.SetIsSelected(NgTodayButton, valid && start == today && end == today);
        System.Windows.Controls.Primitives.Selector.SetIsSelected(NgLastWeekButton, valid && start == today.AddDays(-6) && end == today);
    }

    private void ClearNgOverviewResult(string message)
    {
        ngProgress.Invalidate(message);
        NgOverviewDays.ItemsSource = null;
        UpdateNgOverviewCounts();
        ShowNgOverviewMessage(message);
    }

    private void UpdateNgOverviewCounts()
    {
        NgAllCount.Text = ngProgress.HasResult ? $" {ngProgress.Total}" : "";
        NgPendingCount.Text = ngProgress.HasResult ? $" {ngProgress.Pending}" : "";
        NgDoneCount.Text = ngProgress.HasResult ? $" {ngProgress.Annotated}" : "";
    }

    private async Task RefreshNgOverviewAsync(CancellationToken ct)
    {
        if (ngOverviewPath is not { } path || NgOverviewPanel.Visibility != Visibility.Visible) return;
        string startText = NgStartDate.Text.Trim();
        string endText = NgEndDate.Text.Trim();
        var endpoint = Endpoint;
        int version = navigationVersion;
        bool Current() => ReferenceEquals(client, endpoint) && version == navigationVersion
            && path == ngOverviewPath && NgOverviewPanel.Visibility == Visibility.Visible
            && startText == NgStartDate.Text.Trim() && endText == NgEndDate.Text.Trim();
        await ngProgress.QueryAsync(startText, endText, endpoint.GetNgOverviewAsync, ct, Current);
        if (!Current()) return;
        UpdateNgOverviewCounts();
        RenderNgOverview(keepScroll: true);
        NgOverviewDays.ItemsSource = ngProgress.HasResult ? ngProgress.Rows : null;
        ShowNgOverviewMessage(ngProgress.Message);
    }

    private void ShowNgOverviewMessage(string message)
    {
        NgOverviewMessage.Text = message;
        NgOverviewMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderNgOverview(bool keepScroll)
    {
        ngProgress.SetFilter(NgPendingFilter.IsChecked == true ? NgProgressFilter.Pending
            : NgDoneFilter.IsChecked == true ? NgProgressFilter.Annotated : NgProgressFilter.All);
        NgOverviewDays.ItemsSource = ngProgress.HasResult ? ngProgress.Rows : null;
        if (!keepScroll) NgOverviewScroll.ScrollToTop();
        ShowNgOverviewMessage(ngProgress.Message);
    }

    private async void NgBatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RemoteNgBatch batch })
            await OpenAnnotationAsync(new(batch.Name, batch.Path, true, 0, default, default));
    }

    private async Task FocusNextNgBatchAsync(string completedFolder)
    {
        if (!ngOverviewHasResult || NgDoneFilter.IsChecked == true
            || NgOverviewPanel.Visibility != Visibility.Visible) return;
        var next = ngProgress.FindNext(completedFolder);
        if (next is null)
        {
            SetStatus("当前查询范围内的 NG 批次均已标注", "SuccessBrush");
            return;
        }
        var rows = NgOverviewDays.ItemsSource as IEnumerable<NgDayRow>;
        var nextDay = rows?.FirstOrDefault(day => day.VisibleBatches.Any(batch => batch.Path == next.Path));
        if (nextDay is null) return;
        NgOverviewDays.UpdateLayout();
        var dayContainer = NgOverviewDays.ItemContainerGenerator.ContainerFromItem(nextDay);
        if (dayContainer is null || FindVisualChild<Expander>(dayContainer) is not { } expander) return;
        expander.IsExpanded = true;
        ngProgress.RememberExpanded(nextDay.Date, true);
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(rows, NgOverviewDays.ItemsSource) || annotationWindow is not null
                || busy || !NgOverviewPanel.IsVisible) return;
            NgOverviewDays.UpdateLayout();
            var button = FindVisualChild<Button>(expander,
                button => button.DataContext is RemoteNgBatch batch && batch.Path == next.Path);
            if (button is null) return;
            button.BringIntoView();
            button.Focus();
            SetStatus($"下一未标注批次：{nextDay.Date:yyyy.MM.dd} / {next.Name}，按 Enter 打开", "MutedBrush");
        }, DispatcherPriority.Loaded);
    }

    private void UpdateAnnotateButton()
    {
        NgOverviewPanel.IsEnabled = !IsBusy && annotationWindow is null;
        var selected = FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
        // 标注只针对 NG 的对线：必须勾选「只看NG」，且选中的是日期目录下的批次文件夹。
        bool annotatable = NgFilterEnabled && selected.Length == 1 && IsAnnotatableEntry(selected[0]);
        AnnotateButton.IsEnabled = CanOpenAnnotation && annotatable;
        AnnotateButton.ToolTip = annotationWindow is not null
            ? "请先完成当前标注"
            : annotatable
            ? "为这个对线文件夹添加失败原因分析"
            : !NgFilterEnabled ? "请先勾选「只看NG」"
            : selected.Length > 1 ? "请只选择一个对线文件夹"
            : "请进入 机台/日期 后选择一个对线文件夹";
    }
    private static bool IsAnnotatableEntry(RemoteEntry entry) => entry.IsDirectory
        ? IsBatchFolder(entry.Path)
        : entry.IsImage && IsBatchFolder(ParentPath(entry.Path));
    // 父目录是 yyyyMMdd 的文件夹才是「一次对线」。
    private static bool IsBatchFolder(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && DateTime.TryParseExact(segments[^2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
    private static bool IsDateDirectory(string path) => ParentPath(path).Length > 0
        && DateTime.TryParseExact(path[(path.LastIndexOf('/') + 1)..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private async void Annotate_Click(object sender, RoutedEventArgs e)
    {
        if (AnnotateButton.IsEnabled && FileGrid.SelectedItem is RemoteEntry entry)
            await OpenAnnotationAsync(entry);
    }
    // 标注窗口：左栏列出该对线文件夹下的全部图片，右栏是失败原因分析表单。
    public Task OpenAnnotationAsync(RemoteEntry entry) => CanOpenAnnotation
        ? ExecuteAsync(() => ShowAnnotationWindowAsync(entry))
        : Task.CompletedTask;
    private async Task ShowAnnotationWindowAsync(RemoteEntry entry)
    {
        var endpoint = Endpoint;
        string folder = entry.IsDirectory ? entry.Path : ParentPath(entry.Path);
        var listing = await endpoint.CallAsync(new("list", folder));
        var images = (listing.Entries ?? []).Where(item => item.IsImage).ToArray();
        if (images.Length == 0) { SetStatus("该对线文件夹下没有图片", "WarningBrush"); return; }
        string selected = entry.IsDirectory ? images[0].Path : entry.Path;
        if (!images.Any(item => item.Path == selected)) { SetStatus("图片已从目录移除，请刷新后重试", "WarningBrush"); return; }
        RemoteAnnotation[] history = [];
        try { history = await endpoint.GetAnnotationHistoryAsync(folder); }
        catch (Exception ex)
        {
            // 旧服务端没有标注命令时仍然允许查看，右栏显示未标注。
            Log.Warning(ex, "读取对线标注失败；目录 {RemoteDirectory}", folder);
        }
        if (!ReferenceEquals(client, endpoint)) return;
        string displayPath = (listing.RootName ?? "共享根目录") + "/" + folder;
        var window = new ImageViewer(endpoint, images, selected, new AnnotationContext(folder, displayPath, history));
        var owner = Window.GetWindow(this);
        window.Owner = owner;
        string listDirectory = ParentPath(folder);
        string? openedOverview = NgOverviewPanel.Visibility == Visibility.Visible ? ngOverviewPath : null;
        int openedVersion = navigationVersion;
        window.Closed += async (_, _) =>
        {
            try
            {
                if (IsLoaded && owner is { IsVisible: true })
                {
                    if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                    owner.Activate();
                }
                if (ReferenceEquals(client, endpoint) && (directory == listDirectory || directory == folder))
                    await RefreshAnnotationStatusAsync(directory, CancellationToken.None);
                if (ReferenceEquals(client, endpoint) && ngOverviewPath is { } overview
                    && folder.StartsWith(overview + "/", StringComparison.OrdinalIgnoreCase))
                    await RefreshNgOverviewAsync(CancellationToken.None);
            }
            catch (Exception ex) { Log.Debug(ex, "标注窗口关闭后刷新状态失败"); }
            finally
            {
                annotationWindow = null;
                UpdateAnnotateButton();
            }
            if (window.AnnotationSubmitted && ReferenceEquals(client, endpoint)
                && openedOverview is not null && openedOverview == ngOverviewPath && openedVersion == navigationVersion)
            {
                try { await FocusNextNgBatchAsync(folder); }
                catch (Exception ex) { Log.Debug(ex, "定位下一未标注批次失败；目录 {RemoteFolder}", folder); }
            }
        };
        annotationWindow = window;
        try { window.Show(); }
        catch
        {
            annotationWindow = null;
            throw;
        }
        Log.Information("打开标注窗口；对线文件夹 {RemoteFolder}，图片数 {ImageCount}，标注记录数 {AnnotationCount}",
            folder, images.Length, history.Length);
        SetStatus("标注：" + (Path.GetFileName(folder.Replace('/', '\\'))), "MutedBrush");
    }
    // 只看 NG 时，日期层显示批次状态，批次层显示每张图片的状态。
    private async Task RefreshAnnotationStatusAsync(string path, CancellationToken ct)
    {
        var column = FileGrid.Columns[^1];
        bool imageList = IsDateDirectory(ParentPath(path));
        if (!NgFilterEnabled || (!imageList && !IsDateDirectory(path)))
        {
            annotationIndex = null;
            annotationReadError = null;
            column.Visibility = Visibility.Collapsed;
            ApplyAnnotationStatuses(entries);
            FileGrid.Items.Refresh();
            UpdateAnnotateButton();
            return;
        }
        int version = navigationVersion;
        try
        {
            var items = imageList ? await Endpoint.GetAnnotationHistoryAsync(path, ct) : await Endpoint.ListAnnotationsAsync(path, ct);
            if (version != navigationVersion || !NgFilterEnabled) return;
            var key = imageList ? (Func<RemoteAnnotation, string?>)(item => item.ImageName)
                : item => BatchNames.Normalize(item.BatchNumber);
            annotationIndex = items.Where(item => !string.IsNullOrEmpty(key(item)))
                .GroupBy(item => key(item)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            annotationReadError = null;
        }
        catch (Exception ex)
        {
            if (version != navigationVersion || !NgFilterEnabled) return;
            // 服务端不支持或数据库出错时，列仍然保留并标记为未知，同时在状态栏提示原因。
            Log.Warning(ex, "读取标注状态失败；目录 {RemoteDirectory}", path);
            annotationIndex = null;
            annotationReadError = "读取标注状态失败：" + ex.Message;
            SetStatus("读取标注状态失败：" + ex.Message, "WarningBrush");
        }
        ApplyAnnotationStatuses(entries);
        column.Visibility = Visibility.Visible;
        FileGrid.Items.Refresh();
        UpdateAnnotateButton();
    }
    private void ApplyAnnotationStatuses(IEnumerable<RemoteEntry> rows)
    {
        bool imageList = IsDateDirectory(ParentPath(directory));
        foreach (var entry in rows)
        {
            if (!(imageList ? entry.IsImage : entry.IsDirectory))
            {
                entry.AnnotationStatus = entry.AnnotationTip = "";
                continue;
            }
            // 目录名带服务器前缀（181_53），历史记录是纯编号（53），按编号匹配保证都能点亮。
            string lookup = imageList ? entry.Name : BatchNames.Normalize(entry.Name);
            if (annotationIndex is not null && annotationIndex.TryGetValue(lookup, out var annotation))
            {
                entry.AnnotationStatus = "已标注";
                entry.AnnotationTip = $"{annotation.CategoryName} / {annotation.PlanName}\n{annotation.Operator} · {AnnotationContext.GetDisplayTime(annotation.UpdatedAt):yyyy-MM-dd HH:mm}";
            }
            else
            {
                entry.AnnotationStatus = annotationReadError is not null ? "未知" : annotationIndex is not null ? "未标注" : "";
                entry.AnnotationTip = annotationReadError ?? entry.AnnotationStatus;
            }
        }
    }
    private async Task SelectAllAsync()
    {
        int version = navigationVersion;
        var listing = entries;
        if (FileGrid.SelectionMode == DataGridSelectionMode.Single) return;
        if (!allLoaded)
        {
            SetStatus("正在加载全部条目…", "MutedBrush");
            if (!await LoadAllPagesAsync()) return;
        }
        if (version != navigationVersion || !ReferenceEquals(listing, entries)) return;
        if (FileGrid.SelectionMode == DataGridSelectionMode.Single) return;
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
                await ExecuteAsync(() => NavigateAsync(ParentPath(directory), revealReturnedFolder: true));
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
                if (exists && !Dialog.Confirm(Window.GetWindow(this), $"服务端已存在 {name}，是否覆盖？", "上传覆盖")) continue;
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
            navigation.InvalidateCache();
            await NavigateAsync(targetDirectory, false, ct);
            SetProgress(100, "上传完成");
            SetStatus(uploaded > 0 ? "上传完成" : "未上传文件", uploaded > 0 ? "SuccessBrush" : "MutedBrush");
            if (uploaded > 0) Toast.Show(Window.GetWindow(this), $"已上传 {uploaded} 个文件");
        }, cancellable: true);
    }
    private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        bool blocked = FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(source) is not null;
        var row = ItemsControl.ContainerFromElement(FileGrid, source) as DataGridRow;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        dragArmed = !IsBusy && !blocked && control && e.ClickCount < 2 && row?.Item is RemoteEntry;
        // Ctrl 点击已选中的行会被 DataGrid 切换为未选中，拖动时保留按下前的选择。
        dragSelection = dragArmed && row?.IsSelected == true ? FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray() : null;
        dragOrigin = e.GetPosition(FileGrid);
        Log.Debug("拖拽准备；行内 {OnRow}，Ctrl {Control}，允许 {Armed}，选中 {Count}",
            row is not null, control, dragArmed, FileGrid.SelectedItems.Count);
    }
    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        dragArmed = false;
        dragSelection = null;
        if (dragInProgress) e.Handled = true;
    }
    private async void Grid_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragInProgress) { e.Handled = true; return; }
        if (!dragArmed || busy || client is null) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            dragArmed = false;
            dragSelection = null;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(FileGrid);
        if (Math.Abs(current.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragArmed = false;
        var selected = dragSelection ?? FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
        dragSelection = null;
        if (selected.Length == 0) return;
        if (FileGrid.SelectionMode == DataGridSelectionMode.Single) FileGrid.SelectedItem = selected[0];
        else
            foreach (var entry in selected)
                if (!FileGrid.SelectedItems.Contains(entry)) FileGrid.SelectedItems.Add(entry);
        e.Handled = true;
        var endpoint = Endpoint;
        bool copyToClipboard = IsElevated();
        bool copyOnRelease = false;
        dragInProgress = true;
        UpdateAnnotateButton();
        try
        {
            using var preview = new RemoteDragPreview(endpoint, selected, Window.GetWindow(this), copyToClipboard);
            preview.Show();
            if (copyToClipboard) copyOnRelease = await preview.WaitForReleaseAsync();
            else
            {
                Log.Information("开始拖出远程文件；数量 {Count}", selected.Length);
                RemoteFileDataObject.Start(endpoint, selected, preview.UpdatePosition);
            }
        }
        catch (Exception ex) { Log.Error(ex, "发起远程文件拖拽异常"); }
        finally
        {
            dragInProgress = false;
            FileGrid.ReleaseMouseCapture();
            UpdateAnnotateButton();
        }
        if (copyOnRelease && IsLoaded && ReferenceEquals(client, endpoint)) await CopyToClipboardAsync(selected);
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
                if (exists && !Dialog.Confirm(Window.GetWindow(this), $"服务端已存在 {fileName}，是否覆盖？", "拖入上传")) return;
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
            navigation.InvalidateCache();
            await NavigateAsync(targetDirectory, false, ct);
            SetStatus($"已拖入 {paths.Length} 项", "SuccessBrush");
            Toast.Show(Window.GetWindow(this), $"已拖入 {paths.Length} 项");
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
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row) return;
        // 与资源管理器一致：右键已选中的行保留多选，右键未选中的行改为只选该行。
        if (!row.IsSelected) FileGrid.SelectedItem = row.Item;
        row.Focus();
    }
    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGridRow { Item: RemoteEntry entry } row) return;
        row.ContextMenu = BuildMenu(entry, showCopyShortcut: true);
        if (row.ContextMenu == null) e.Handled = true;
    }
    private MenuItem CreateFileMenuItem(string header, string iconKey)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Style = (Style)FindResource("IconGlyph"),
            Data = (Geometry)FindResource(iconKey)
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        return new MenuItem { Header = header, Icon = icon };
    }
    private ContextMenu BuildMenu(RemoteEntry entry, bool showCopyShortcut = false)
    {
        var menu = new ContextMenu();
        var separatorStyle = (Style)FindResource("FileMenuSeparator");
        var copy = CreateFileMenuItem("复制", "IconRestoreGeometry");
        if (showCopyShortcut) copy.InputGestureText = "Ctrl+C";
        copy.Click += async (_, _) =>
        {
            var selected = FileGrid.SelectedItems.Cast<RemoteEntry>().ToArray();
            await CopyToClipboardAsync(selected.Contains(entry) ? selected : [entry]);
        };
        menu.Items.Add(copy);
        if (IsAnnotatableEntry(entry))
        {
            menu.Items.Add(new Separator { Style = separatorStyle });
            var annotate = CreateFileMenuItem("标注", "BrowserAnnotateGeometry");
            menu.Opened += (_, _) => annotate.IsEnabled = CanOpenAnnotation;
            annotate.Click += async (_, _) => await OpenAnnotationAsync(entry);
            menu.Items.Add(annotate);
        }
        if (!entry.IsDirectory)
        {
            menu.Items.Add(new Separator { Style = separatorStyle });
            var delete = CreateFileMenuItem("删除服务端文件…", "BrowserDeleteGeometry");
            delete.Click += async (_, _) =>
            {
                if (busy || client == null) return;
                Log.Information("请求确认删除服务端文件；路径 {RemotePath}", entry.Path);
                if (!Dialog.ConfirmDanger(Window.GetWindow(this), $"确定永久删除服务端文件？\n\n{entry.Path}\n\n此操作会删除服务端磁盘上的文件，无法通过本程序恢复。", "删除服务端文件"))
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
        navigation.InvalidateCache();
        await NavigateAsync(parent, parent.Length == 0, ct);
        SetProgress(100, "压缩完成");
        SetStatus("压缩完成：" + archive, "SuccessBrush");
    }, cancellable: true);
    // 界面菜单确认后调用；公开方法便于同一界面流程的自动化验证。
    public Task DeleteEntryAsync(RemoteEntry entry) => ExecuteAsync(async () =>
    {
        await Endpoint.DeleteFileAsync(entry);
        string parent = ParentPath(entry.Path);
        navigation.InvalidateCache();
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
        if (entry.IsDirectory) { navigation.InvalidateCache(); await NavigateAsync(ParentPath(entry.Path), ParentPath(entry.Path).Length == 0, ct); }
        SetProgress(100, "下载完成");
        SetStatus("下载完成，SHA-256 校验通过", "SuccessBrush");
    }, cancellable: true);
    // 按用户和进程隔离，避免多个客户端实例互相删除正在使用的剪贴板会话。
    private static readonly string ClipboardTempRoot = Path.Combine(
        AppContext.BaseDirectory, "Clipboard", Environment.UserName, Environment.ProcessId.ToString());
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
        Toast.Show(Window.GetWindow(this), selected.Count == 1 ? $"已复制 {selected[0].Name}" : $"已复制 {selected.Count} 项");
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
        CleanClipboardTemp();
        CleanStaleClipboardRoots();
        string session = Path.Combine(ClipboardTempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        return session;
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
        if (IsBusy) return;
        dragArmed = false;
        dragSelection = null;
        busy = true;
        UpdateAnnotateButton();
        FileGrid.IsEnabled = false; FolderTree.IsEnabled = false; FilterHost.IsEnabled = false; FilterRightHost.IsEnabled = false; NavHost.IsEnabled = false;
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
            if (StatusText.Text == BusyText.Text) SetStatus("就绪");
            UploadProgress.IsIndeterminate = false;
            HideProgress();
            HideBusy();
            CancelButton.Visibility = Visibility.Collapsed;
            operationCancel = null;
            busy = false;
            FileGrid.IsEnabled = true; FolderTree.IsEnabled = true; FilterHost.IsEnabled = true; FilterRightHost.IsEnabled = true; NavHost.IsEnabled = true;
            UpdateAnnotateButton();
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
        StatusChanged?.Invoke(text, brushKey);
    }
    private void ShowBusy(string text)
    {
        BusyProgress.IsIndeterminate = true;
        BusyProgress.Value = 0;
        BusyText.Text = text;
        SetStatus(text);
        busyIndicatorTimer.Start();
    }
    private void SetBusyProgress(double percent, string text)
    {
        BusyProgress.IsIndeterminate = false;
        BusyProgress.Value = Math.Clamp(percent, 0, 100);
        BusyText.Text = text;
    }
    private void DisplayBusyIndicator()
    {
        busyIndicatorTimer.Stop();
        BusyIndicator.Visibility = Visibility.Visible;
    }
    private void HideBusy()
    {
        busyIndicatorTimer.Stop();
        BusyIndicator.Visibility = Visibility.Collapsed;
        BusyProgress.IsIndeterminate = true;
        BusyProgress.Value = 0;
    }
    private void ShowProgress(string text)
    {
        UploadText.Text = text;
        UploadText.ToolTip = text;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressChanged?.Invoke(text, 0, false, true);
    }
    private void SetProgress(double value, string text, bool indeterminate = false)
    {
        UploadProgress.IsIndeterminate = indeterminate;
        if (!indeterminate) UploadProgress.Value = value;
        UploadText.Text = text;
        UploadText.ToolTip = text;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressChanged?.Invoke(text, value, indeterminate, true);
    }
    private void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        UploadText.Text = "";
        ProgressChanged?.Invoke("", 0, false, false);
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
        return GetIcon(entry);
    }

    public static ImageSource? GetIcon(RemoteEntry entry, bool large = false)
    {
        string extension = entry.IsDirectory ? "<folder>" : Path.GetExtension(entry.Name);
        string key = extension + (large ? ":large" : ":small");
        if (Cache.TryGetValue(key, out var cached)) return cached;
        string path = entry.IsDirectory ? "folder" : "file" + extension;
        const uint useAttributes = 0x10, icon = 0x100, smallIcon = 0x1;
        uint flags = useAttributes | icon | (large ? 0x4000u /* SYSICONINDEX */ : smallIcon);
        if (SHGetFileInfo(path, entry.IsDirectory ? 0x10u : 0x80u, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            if (large)
            {
                IShellImageList? list = null;
                try
                {
                    var iid = typeof(IShellImageList).GUID;
                    if (SHGetImageList(4 /* SHIL_JUMBO */, ref iid, out list) >= 0
                        && list.GetIcon(info.IconIndex, 1, out var jumbo) >= 0 && jumbo != IntPtr.Zero)
                    {
                        DestroyIcon(info.Icon);
                        info.Icon = jumbo;
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "读取大文件图标失败；扩展名 {Extension}", extension); }
                finally { if (list is not null) Marshal.ReleaseComObject(list); }
            }
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); Cache[key] = image; return image;
        }
        finally { DestroyIcon(info.Icon); }
    }
    [DllImport("shell32.dll")] private static extern int SHGetImageList(int size, ref Guid iid, out IShellImageList list);
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int result);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, uint mask, out int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, uint flags, out IntPtr icon);
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

public enum ToastKind { Info, Success, Warning, Danger }

/// <summary>�������½ǵ�������ʾ�����ı����в��֡�</summary>
public static class Toast
{
    public static void Show(Window? owner, string message, ToastKind kind = ToastKind.Success, int milliseconds = 2200)
    {
        if (owner?.Content is not Visual visual) return;
        var layer = AdornerLayer.GetAdornerLayer(visual);
        if (layer is null) return;
        Color color = kind switch
        {
            ToastKind.Success => Color.FromRgb(0x4C, 0xC3, 0x8A),
            ToastKind.Warning => Color.FromRgb(0xE5, 0xB5, 0x67),
            ToastKind.Danger => Color.FromRgb(0xE0, 0x6C, 0x75),
            _ => Color.FromRgb(0x8A, 0xB4, 0xF8)
        };
        var content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x21, 0x24)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 7, 14, 7),
            Opacity = 0,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.16, Color = Colors.Black },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 12, MaxWidth = 440, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
        var adorner = new ToastAdorner(visual as UIElement ?? owner, content);
        layer.Add(adorner);
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!owner.IsVisible)
            {
                try { layer.Remove(adorner); } catch (Exception ex) { Log.Debug(ex, "移除窗口关闭后的提示失败"); }
                return;
            }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) =>
            {
                try { layer.Remove(adorner); } catch (Exception ex) { Log.Debug(ex, "移除提示失败"); }
                content.BeginAnimation(UIElement.OpacityProperty, null);
            };
            content.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        timer.Start();
    }

    private sealed class ToastAdorner : Adorner
    {
        private readonly UIElement child;
        public ToastAdorner(UIElement adorned, UIElement child) : base(adorned)
        {
            this.child = child;
            AddVisualChild(child);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => child;
        protected override Size MeasureOverride(Size constraint)
        {
            child.Measure(constraint);
            // 覆盖整个被装饰元素，才能把提示定位到右下角。
            return AdornedElement.RenderSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            child.Measure(finalSize);
            var size = child.DesiredSize;
            child.Arrange(new Rect(
                Math.Max(0, finalSize.Width - size.Width - 20),
                Math.Max(0, finalSize.Height - size.Height - 20),
                size.Width, size.Height));
            return finalSize;
        }
    }
}
