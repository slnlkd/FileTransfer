using System.IO;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Data;
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
    public bool IsBusy => busy;
    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(FileGrid.ItemsSource) is not ListCollectionView view) return;
        if (e.Column.SortMemberPath != "Name") { view.CustomSort = null; return; }
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        view.CustomSort = new NaturalNameComparer(direction);
        e.Column.SortDirection = direction;
        e.Handled = true;
    }
    private sealed class NaturalNameComparer(ListSortDirection direction) : IComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string left, string right);
        public int Compare(object? x, object? y)
        {
            int result = StrCmpLogicalW(((RemoteEntry)x!).Name, ((RemoteEntry)y!).Name);
            return direction == ListSortDirection.Descending ? -Math.Sign(result) : Math.Sign(result);
        }
    }
    public RemoteBrowser()
    {
        InitializeComponent();
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new EventSetter(ContextMenuOpeningEvent, new ContextMenuEventHandler(Row_ContextMenuOpening)));
        style.Setters.Add(new EventSetter(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(Row_RightClick)));
        // 占位菜单确保 WPF 会引发 ContextMenuOpening；打开时按当前行生成真实菜单。
        style.Setters.Add(new Setter(ContextMenuProperty, new ContextMenu()));
        FileGrid.RowStyle = style;
    }
    public async Task ConnectAsync(RemoteFileClient endpoint)
    {
        if (busy) throw new IOException("当前操作尚未结束，请稍后重新连接");
        client = endpoint; directory = "";
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        await NavigateAsync("", true);
    }
    public void Disconnect() { client = null; FolderTree.Items.Clear(); FileGrid.ItemsSource = null; PathText.Text = "尚未连接"; }
    private RemoteFileClient Endpoint => client ?? throw new IOException("请先连接服务端");
    private async Task NavigateAsync(string path, bool rebuild = false)
    {
        var response = await Endpoint.CallAsync(new("list", path));
        if (!string.Equals(directory, path, StringComparison.Ordinal))
        {
            foreach (var column in FileGrid.Columns) column.SortDirection = null;
            FileGrid.Columns[0].SortDirection = ListSortDirection.Ascending;
        }
        var nameDirection = FileGrid.Columns[0].SortDirection;
        directory = path; entries = response.Entries ?? [];
        FileGrid.ItemsSource = entries;
        if (nameDirection is { } direction && CollectionViewSource.GetDefaultView(entries) is ListCollectionView view)
        {
            view.CustomSort = new NaturalNameComparer(direction);
            FileGrid.Columns[0].SortDirection = direction;
        }
        PathText.Text = $"{Endpoint.Host}:{Endpoint.Port} / {response.RootName} / {directory}";
        StatusText.Text = $"共 {entries.Length} 项；双击文本编辑、图片浏览或 EXE 管理运行状态。";
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
        else if (FindNode(FolderTree.Items, path) is TreeViewItem node) SetChildren(node, entries);
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
        var node = new TreeViewItem { Header = (entry.IsDirectory ? "📁 " : "📄 ") + entry.Name, Tag = entry };
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
        else { StatusText.Text = "此文件类型暂不提供预览或编辑：" + entry.Kind; return; }
        viewer.Owner = Window.GetWindow(this); viewer.Show();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        // 根目录重建可移除树中的过期节点；目录内刷新只更新当前节点。
        await NavigateAsync(directory, directory.Length == 0);
    });
    private async void Parent_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => NavigateAsync(ParentPath(directory)));
    private static string ParentPath(string path) { int index = path.LastIndexOf('/'); return index < 0 ? "" : path[..index]; }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (client == null) { StatusText.Text = "请先连接服务端"; return; }
        var dialog = new OpenFileDialog { Multiselect = true, Title = "上传文件到服务端当前目录" };
        if (dialog.ShowDialog() != true) return;
        var endpoint = Endpoint; string targetDirectory = directory;
        await ExecuteAsync(async () =>
        {
            foreach (string local in dialog.FileNames)
            {
                string name = Path.GetFileName(local);
                bool exists = entries.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (exists && MessageBox.Show(Window.GetWindow(this), $"服务端已存在 {name}，是否覆盖？", "上传覆盖", MessageBoxButton.YesNo) != MessageBoxResult.Yes) continue;
                string target = targetDirectory.Length == 0 ? name : targetDirectory + "/" + name;
                var progress = new Progress<double>(value => { UploadProgress.Value = value; StatusText.Text = $"正在上传 {name}：{value:F1}%"; });
                await endpoint.UploadAsync(local, target, exists, progress);
            }
            await NavigateAsync(targetDirectory);
            StatusText.Text = "上传完成。更新 EXE 后可双击程序，点击启动或重新启动。";
        });
    }
    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        string name = NewFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") throw new IOException("请输入有效的单级文件夹名称");
        await Endpoint.CallAsync(new("mkdir", directory.Length == 0 ? name : directory + "/" + name));
        NewFolderBox.Clear(); await NavigateAsync(directory);
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
        UploadProgress.IsIndeterminate = value.Stage is "扫描目录" or "等待压缩";
        UploadProgress.Value = value.Percent;
        StatusText.Text = $"{value.Stage}：{value.Path}  {value.Percent:F1}%  {value.CompletedBytes / 1048576d:F1} / {value.TotalBytes / 1048576d:F1} MiB";
    });
    public Task AddArchiveAsync(RemoteEntry entry) => ExecuteAsync(async () =>
    {
        UploadProgress.Value = 0;
        string archive = await Endpoint.CompressAsync(entry.Path, ArchiveProgress());
        // 刷新 ZIP 所在目录，根目录压缩的结果放在根目录内。
        string parent = ParentPath(entry.Path);
        await NavigateAsync(parent, parent.Length == 0);
        UploadProgress.Value = 100; StatusText.Text = "压缩完成：" + archive + "；右键 ZIP 可下载。";
    });
    // 界面菜单确认后调用；公开方法便于同一界面流程的自动化验证。
    public Task DeleteEntryAsync(RemoteEntry entry) => ExecuteAsync(async () =>
    {
        await Endpoint.DeleteFileAsync(entry);
        string parent = ParentPath(entry.Path);
        await NavigateAsync(parent, parent.Length == 0);
        StatusText.Text = "已删除服务端文件：" + entry.Path;
    });
    public Task DownloadEntryAsync(RemoteEntry entry, string destination) => ExecuteAsync(async () =>
    {
        if (!entry.IsDirectory && !entry.IsZip) throw new IOException("请选择文件夹或 ZIP");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("本地目标已存在，请更换保存名称");
        var endpoint = Endpoint; var progress = ArchiveProgress(); UploadProgress.Value = 0;
        string archive = entry.IsDirectory ? await endpoint.CompressAsync(entry.Path, progress) : entry.Path;
        await endpoint.DownloadZipAsync(archive, destination, progress);
        if (entry.IsDirectory) await NavigateAsync(ParentPath(entry.Path), ParentPath(entry.Path).Length == 0);
        UploadProgress.Value = 100; StatusText.Text = "下载完成，SHA-256 校验通过：" + destination;
    });
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Log.Error(ex, "远程浏览器操作失败；当前目录 {RemoteDirectory}", directory); StatusText.Text = ex.Message; }
        finally { UploadProgress.IsIndeterminate = false; busy = false; IsEnabled = true; }
    }
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
