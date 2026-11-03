using System.IO;
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
    private RemoteEntry[] entries = [];
    private Window? hostWindow;
    private int pendingUp;
    private bool navigatingUp;
    private string? listFilter;
    private Func<string, string?>? localEmptyText = null;
    private ScrollViewer? fileScroll;
    public bool IsBusy => busy;
    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(FileGrid.ItemsSource) is not ListCollectionView view) return;
        string member = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(member)) { view.CustomSort = null; return; }
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        view.CustomSort = new EntryComparer(member, direction);
        e.Column.SortDirection = direction;
        e.Handled = true;
    }
    private sealed class EntryComparer(string member, ListSortDirection direction) : IComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string left, string right);
        public int Compare(object? x, object? y)
        {
            var left = (RemoteEntry)x!;
            var right = (RemoteEntry)y!;
            // 与资源管理器一致：文件夹始终排在文件前，组内按当前列排序。
            if (left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;
            int result = member switch
            {
                "Name" => StrCmpLogicalW(left.Name, right.Name),
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
        Loaded += Browser_Loaded;
        Unloaded += Browser_Unloaded;
    }
    public async Task ConnectAsync(RemoteFileClient endpoint)
    {
        if (busy) throw new IOException("当前操作尚未结束，请稍后重新连接");
        client = endpoint; directory = "";
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        await NavigateAsync("", true);
    }
    public void Disconnect()
    {
        client = null;
        entries = [];
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
    private async Task NavigateAsync(string path, bool rebuild = false)
    {
        bool ownedBusy = !busy;
        if (ownedBusy) ShowBusy("正在读取目录…");
        else BusyText.Text = "正在读取目录…";
        try
        {
            var response = await Endpoint.CallAsync(new("list", path, Filter: listFilter));
            if (!string.Equals(directory, path, StringComparison.Ordinal))
            {
                foreach (var column in FileGrid.Columns) column.SortDirection = null;
                FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
            }
            var nameDirection = FileGrid.Columns[0].SortDirection;
            string previous = directory;
            directory = path; entries = response.Entries ?? [];
            FileGrid.ItemsSource = entries;
            // 切换目录后回到列表顶部；同一目录刷新保留当前位置。
            if (!string.Equals(previous, path, StringComparison.Ordinal)) ResetFileScroll();
            if (nameDirection is { } direction && CollectionViewSource.GetDefaultView(entries) is ListCollectionView view)
            {
                view.CustomSort = new EntryComparer("Name", direction);
                FileGrid.Columns[0].SortDirection = direction;
            }
            UpdatePath(response);
            ItemCountText.Text = $"{entries.Length} 项";
            SetStatus(response.Warning ?? (entries.Length == 0 ? EmptyTextFor(directory) : "就绪"),
                response.Warning is null ? "MutedBrush" : "WarningBrush");
            UpdateEmptyState();
            if (rebuild || FolderTree.Items.Count == 0)
            {
                rebuilding = true;
                try
                {
                    FolderTree.Items.Clear();
                    var root = MakeNode(new(response.RootName ?? "共享根目录", "", true, 0, default, default));
                    SetChildren(root, entries); root.IsExpanded = true;
                    FolderTree.Items.Add(root);
                }
                finally { rebuilding = false; }
            }
            else if (FindNode(FolderTree.Items, path) is TreeViewItem node)
            {
                // 目录树始终展示未过滤的完整内容，过滤只作用于右侧列表。
                var treeEntries = listFilter is null ? entries : (await Endpoint.CallAsync(new("list", path))).Entries ?? [];
                SetChildren(node, treeEntries);
            }
        }
        finally { if (ownedBusy) HideBusy(); }
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
        EmptyState.Visibility = connected && entries.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = connected ? EmptyTextFor(directory) : "连接后可浏览共享目录";
        EmptyUploadButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }
    private string EmptyTextFor(string path) => localEmptyText?.Invoke(path) ?? "此文件夹为空";
    private void ResetFileScroll()
    {
        fileScroll ??= FindVisualChild<ScrollViewer>(FileGrid);
        if (fileScroll is null) return;
        fileScroll.ScrollToTop();
        // 等待新条目完成布局后再设置一次，避免虚拟化把偏移量恢复成旧位置。
        Dispatcher.BeginInvoke(new Action(() => fileScroll?.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
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
                await ExecuteAsync(async () => SetChildren(node, (await Endpoint.CallAsync(new("list", entry.Path))).Entries ?? []));
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
        // 根目录重建可移除树中的过期节点；目录内刷新只更新当前节点。
        await NavigateAsync(directory, directory.Length == 0);
    });
    private async void Parent_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => NavigateAsync(ParentPath(directory)));
    private static string ParentPath(string path) { int index = path.LastIndexOf('/'); return index < 0 ? "" : path[..index]; }
    private void Browser_Loaded(object sender, RoutedEventArgs e)
    {
        hostWindow = Window.GetWindow(this);
        if (hostWindow is null) return;
        hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        hostWindow.PreviewKeyDown += HostWindow_PreviewKeyDown;
    }
    private void Browser_Unloaded(object sender, RoutedEventArgs e)
    {
        if (hostWindow is null) return;
        hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        hostWindow = null;
    }
    private async void HostWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        if (!FileGrid.IsKeyboardFocusWithin) return;
        e.Handled = true;
        await HandleBackspaceAsync();
    }
    private async void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Back) return;
        e.Handled = true;
        await HandleBackspaceAsync();
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
        if (client == null) { SetStatus("请先连接服务端", "DangerBrush"); return; }
        var dialog = new OpenFileDialog { Multiselect = true, Title = "上传文件到服务端当前目录" };
        if (dialog.ShowDialog() != true) return;
        var endpoint = Endpoint; string targetDirectory = directory;
        await ExecuteAsync(async () =>
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
                await endpoint.UploadAsync(local, target, exists, progress);
                uploaded++;
            }
            await NavigateAsync(targetDirectory);
            SetProgress(100, "上传完成");
            SetStatus(uploaded > 0 ? "上传完成" : "未上传文件", uploaded > 0 ? "SuccessBrush" : "MutedBrush");
        });
    }
    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        string name = NewFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") throw new IOException("请输入有效的单级文件夹名称");
        await Endpoint.CallAsync(new("mkdir", directory.Length == 0 ? name : directory + "/" + name));
        NewFolderBox.Clear(); await NavigateAsync(directory);
        SetStatus("已新建文件夹", "SuccessBrush");
    });
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row) { row.IsSelected = true; row.Focus(); }
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
        var compress = new MenuItem { Header = "添加压缩包" };
        compress.Click += async (_, _) => await AddArchiveAsync(entry);
        menu.Items.Add(compress);
        if (entry.IsDirectory || entry.IsZip)
        {
            var download = new MenuItem { Header = entry.IsDirectory ? "打包下载文件夹…" : "下载 ZIP…" };
            download.Click += async (_, _) =>
            {
                if (busy || client == null) return;
                var dialog = new SaveFileDialog { Title = "保存下载的 ZIP 压缩包", Filter = "ZIP 压缩包|*.zip", FileName = entry.IsDirectory ? entry.Name + ".zip" : entry.Name, DefaultExt = ".zip", AddExtension = true, OverwritePrompt = false };
                if (dialog.ShowDialog(Window.GetWindow(this)) == true) await DownloadEntryAsync(entry, dialog.FileName);
            };
            menu.Items.Add(download);
        }
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
    public Task AddArchiveAsync(RemoteEntry entry) => ExecuteAsync(async () =>
    {
        BusyText.Text = "正在压缩…";
        SetProgress(0, "准备压缩");
        string archive = await Endpoint.CompressAsync(entry.Path, ArchiveProgress());
        // 刷新 ZIP 所在目录，根目录压缩的结果放在根目录内。
        string parent = ParentPath(entry.Path);
        await NavigateAsync(parent, parent.Length == 0);
        SetProgress(100, "压缩完成");
        SetStatus("压缩完成：" + archive, "SuccessBrush");
    });
    // 界面菜单确认后调用；公开方法便于同一界面流程的自动化验证。
    public Task DeleteEntryAsync(RemoteEntry entry) => ExecuteAsync(async () =>
    {
        await Endpoint.DeleteFileAsync(entry);
        string parent = ParentPath(entry.Path);
        await NavigateAsync(parent, parent.Length == 0);
        SetStatus("已删除：" + entry.Path, "SuccessBrush");
    });
    public Task DownloadEntryAsync(RemoteEntry entry, string destination) => ExecuteAsync(async () =>
    {
        if (!entry.IsDirectory && !entry.IsZip) throw new IOException("请选择文件夹或 ZIP");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("本地目标已存在，请更换保存名称");
        BusyText.Text = "正在下载…";
        var endpoint = Endpoint; var progress = ArchiveProgress(); SetProgress(0, "准备下载");
        string archive = entry.IsDirectory ? await endpoint.CompressAsync(entry.Path, progress) : entry.Path;
        await endpoint.DownloadZipAsync(archive, destination, progress);
        if (entry.IsDirectory) await NavigateAsync(ParentPath(entry.Path), ParentPath(entry.Path).Length == 0);
        SetProgress(100, "下载完成");
        SetStatus("下载完成，SHA-256 校验通过", "SuccessBrush");
    });
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        ShowBusy("正在处理…");
        try { await action(); }
        catch (Exception ex) { Log.Error(ex, "远程浏览器操作失败；当前目录 {RemoteDirectory}", directory); SetStatus(ex.Message, "DangerBrush"); }
        finally
        {
            UploadProgress.IsIndeterminate = false;
            HideProgress();
            HideBusy();
            busy = false; IsEnabled = true;
        }
    }
    private void SetStatus(string text, string brushKey = "MutedBrush")
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        BrowserStatusDot.Fill = ThemeBrush(brushKey);
    }
    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
    }
    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;
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
