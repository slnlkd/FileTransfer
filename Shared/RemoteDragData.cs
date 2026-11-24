using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using Transfer.Core.Remote;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RemoteFiles.Desktop;

/// <summary>
/// 把远程文件/文件夹以 Windows 虚拟文件格式提供给资源管理器拖放。
/// 拖拽开始时不下载内容，等目标进程请求文件内容时再逐个读取。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RemoteFileDataObject : IDataObject
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int FD_ATTRIBUTES = 0x4;
    private const int FD_FILESIZE = 0x40;
    private const int FD_WRITESTIME = 0x20;
    private const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly int DescriptorFormat = Native.RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly int ContentsFormat = Native.RegisterClipboardFormat("FileContents");
    private static readonly int DropEffectFormat = Native.RegisterClipboardFormat("Preferred DropEffect");

    private static readonly string SessionRoot = Path.Combine(AppContext.BaseDirectory, "Drag");

    private readonly RemoteFileClient client;
    private readonly RemoteEntry[] roots;
    private readonly string session;
    private List<Item>? items;
    private readonly List<RemoteFileStream> streams = [];

    public RemoteFileDataObject(RemoteFileClient client, IReadOnlyList<RemoteEntry> roots)
    {
        this.client = client;
        this.roots = roots.ToArray();
        session = Path.Combine(SessionRoot, Guid.NewGuid().ToString("N"));
    }

    public static int FormatId(string name) => Native.RegisterClipboardFormat(name);
    public static int DescriptorFormatId => DescriptorFormat;
    public static int ContentsFormatId => ContentsFormat;

    public IReadOnlyList<string> DescriptorNames() => Items().Select(i => i.Name).ToArray();

    private List<Item> Items()
    {
        if (items is not null) return items;
        var list = new List<Item>();
        foreach (var root in roots)
        {
            if (!root.IsDirectory) { list.Add(new(root.Name, false, root.Length, root.ModifiedUtc, root.Path)); continue; }
            AddDirectory(list, root.Path, root.Name, root.ModifiedUtc);
        }
        items = list;
        Log.Debug("拖拽条目准备完成；根 {Roots}，条目 {Count}", roots.Length, list.Count);
        return list;
    }

    private void AddDirectory(List<Item> list, string remotePath, string name, DateTime modified)
    {
        list.Add(new(name, true, 0, modified, remotePath));
        var entries = Task.Run(() => client.CallAsync(new("list", remotePath))).GetAwaiter().GetResult().Entries ?? [];
        foreach (var child in entries)
        {
            string childName = name + "\\" + child.Name;
            if (child.IsDirectory) AddDirectory(list, child.Path, childName, child.ModifiedUtc);
            else list.Add(new(childName, false, child.Length, child.ModifiedUtc, child.Path));
        }
    }

    private STGMEDIUM DescriptorMedium()
    {
        var list = Items();
        int size = Marshal.SizeOf<FILEDESCRIPTORW>();
        IntPtr memory = Native.GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (UIntPtr)(size * Math.Max(1, list.Count)));
        IntPtr target = Native.GlobalLock(memory);
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var descriptor = new FILEDESCRIPTORW
                {
                    dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME,
                    dwFileAttributes = item.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                    nFileSizeHigh = (uint)(item.Length >> 32),
                    nFileSizeLow = (uint)(item.Length & 0xFFFFFFFF),
                    cFileName = item.Name.Length >= 260 ? item.Name[..259] : item.Name
                };
                long fileTime = item.Modified.ToFileTimeUtc();
                descriptor.ftLastWriteTime = new FILETIME { dwLowDateTime = (int)(fileTime & 0xFFFFFFFF), dwHighDateTime = (int)(fileTime >> 32) };
                Marshal.StructureToPtr(descriptor, IntPtr.Add(target, i * size), false);
            }
        }
        finally { Native.GlobalUnlock(memory); }
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory, pUnkForRelease = null };
    }

    private STGMEDIUM ContentsMedium(int index)
    {
        var list = Items();
        if (index < 0 || index >= list.Count) throw new COMException("文件序号无效", DV_E_FORMATETC);
        var item = list[index];
        if (item.IsDirectory) throw new COMException("文件夹没有文件内容", DV_E_FORMATETC);
        var stream = new RemoteFileStream(client, item.RemotePath, item.Length, Path.Combine(session, index.ToString()));
        streams.Add(stream);
        IntPtr pointer = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
        return new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = pointer, pUnkForRelease = null };
    }

    private static STGMEDIUM DropEffectMedium()
    {
        IntPtr memory = Native.GlobalAlloc(0x0042, (UIntPtr)4);
        IntPtr target = Native.GlobalLock(memory);
        try { Marshal.WriteInt32(target, 1 /* DROPEFFECT_COPY */); }
        finally { Native.GlobalUnlock(memory); }
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory, pUnkForRelease = null };
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        try
        {
            if (Matches(format.cfFormat, DescriptorFormat)) { medium = DescriptorMedium(); return; }
            if (Matches(format.cfFormat, ContentsFormat)) { medium = ContentsMedium(format.lindex); return; }
            if (Matches(format.cfFormat, DropEffectFormat)) { medium = DropEffectMedium(); return; }
        }
        catch (Exception ex) { Log.Error(ex, "拖拽数据请求失败；格式 {Format}，序号 {Index}", format.cfFormat, format.lindex); throw; }
        Log.Warning("拖拽请求了不支持的格式；格式 {Format}", format.cfFormat);
        medium = default;
        throw new COMException("不支持的数据格式", DV_E_FORMATETC);
    }

    private static bool Matches(short clipboardFormat, int registeredFormat) => (ushort)clipboardFormat == (ushort)registeredFormat;

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("不支持", DV_E_FORMATETC);
    public int QueryGetData(ref FORMATETC format)
    {
        bool supported = Matches(format.cfFormat, DescriptorFormat) || Matches(format.cfFormat, ContentsFormat) || Matches(format.cfFormat, DropEffectFormat);
        if (!supported) Log.Debug("拖拽查询不支持的格式；格式 {Format}", format.cfFormat);
        return supported ? 0 : DV_E_FORMATETC;
    }
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return 1 /* DATA_S_SAMEFORMATETC */; }
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new COMException("不支持", DV_E_FORMATETC);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        Log.Debug("拖拽枚举数据格式；方向 {Direction}", direction);
        return new FormatEnumerator(
        new FORMATETC { cfFormat = (short)DescriptorFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
        new FORMATETC { cfFormat = (short)ContentsFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
        new FORMATETC { cfFormat = (short)DropEffectFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL });
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null!; return OLE_E_ADVISENOTSUPPORTED; }

    internal sealed record Item(string Name, bool IsDirectory, long Length, DateTime Modified, string RemotePath);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public int dwFlags;
        public Guid clsid;
        public int dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FormatEnumerator(params FORMATETC[] formats) : IEnumFORMATETC
    {
        private int index;
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats);
        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            int count = 0;
            while (count < celt && index < formats.Length)
            {
                rgelt[count++] = formats[index++];
            }
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = count;
            return count == celt ? 0 : 1;
        }
        public int Skip(int celt) { index = Math.Min(formats.Length, index + celt); return index < formats.Length ? 1 : 0; }
        public int Reset() { index = 0; return 0; }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RemoteFileStream(RemoteFileClient client, string remotePath, long length, string tempBase) : IStream
    {
        private FileStream? stream;
        internal void Ensure()
        {
            if (stream is not null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(tempBase)!);
            string file = tempBase + ".bin";
            Task.Run(() => client.DownloadFileAsync(remotePath, file)).GetAwaiter().GetResult();
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            Ensure();
            int read = stream!.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
        }
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            Ensure();
            long position = stream!.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
        }
        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG
            {
                type = 2 /* STGTY_STREAM */,
                cbSize = length,
                grfMode = 0 /* STGM_READ */
            };
        }
        public void SetSize(long libNewSize) => throw new COMException("只读", unchecked((int)0x80030005));
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new COMException("只读", unchecked((int)0x80030005));
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("只读", unchecked((int)0x80030005));
        public void Clone(out IStream ppstm) => throw new COMException("不支持", unchecked((int)0x80004005));
    }

    private static void CleanOldSessions()
    {
        try
        {
            if (!Directory.Exists(SessionRoot)) return;
            foreach (string directory in Directory.GetDirectories(SessionRoot))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(1)) continue;
                    Directory.Delete(directory, true);
                }
                catch (Exception ex) { Log.Debug(ex, "清理旧拖拽临时目录失败；路径 {Path}", directory); }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "扫描拖拽临时目录失败"); }
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegisterClipboardFormat(string format);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalAlloc(int flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("ole32.dll")]
        internal static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
            [MarshalAs(UnmanagedType.Interface)] IOleDropSource dropSource, int allowedEffects, out int effect);
        [DllImport("ole32.dll")]
        internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    }

    [ComImport, ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class DropSource(Action? feedback = null) : IOleDropSource
    {
        public int QueryContinueDrag(bool escapePressed, int keyState)
        {
            if (escapePressed || (keyState & 8 /* MK_CONTROL */) == 0) return unchecked((int)0x00040101); // DRAGDROP_S_CANCEL
            if ((keyState & 1) == 0) return unchecked((int)0x00040100); // DRAGDROP_S_DROP
            return 0;
        }
        public int GiveFeedback(int effect)
        {
            feedback?.Invoke();
            return unchecked((int)0x00040102); // DRAGDROP_S_USEDEFAULTCURSORS
        }
    }

    public static DragDropEffects Start(RemoteFileClient client, IReadOnlyList<RemoteEntry> entries, Action? feedback = null)
    {
        if (entries.Count == 0) return DragDropEffects.None;
        CleanOldSessions();
        var data = new RemoteFileDataObject(client, entries);
        int effect = 0;
        try
        {
            int hr = Native.DoDragDrop(data, new DropSource(feedback), 1 /* DROPEFFECT_COPY */, out effect);
            Log.Information("远程文件拖拽结束；HRESULT {HResult}，效果 {Effect}，条目 {Count}", hr, effect, entries.Count);
            if (hr < 0) Log.Warning("远程文件拖拽返回失败；HRESULT {HResult}", hr);
            return (DragDropEffects)effect;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发起远程文件拖拽失败");
            return DragDropEffects.None;
        }
        finally { GC.KeepAlive(data); }
    }
}

/// <summary>不接收鼠标和焦点的拖动预览，可跨窗口跟随指针。</summary>
internal sealed class RemoteDragPreview : Window, IDisposable
{
    private readonly Image previewImage = new() { Width = 96, Height = 96, Stretch = Stretch.Uniform };
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer pointerTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private TaskCompletionSource<bool>? released;
    private IntPtr handle;
    private bool closed;
    private bool disposed;
    internal Task ThumbnailReady { get; }

    internal RemoteDragPreview(RemoteFileClient client, IReadOnlyList<RemoteEntry> entries, Window? owner, bool clipboardCopy)
    {
        ThemeLoader.Apply(this);
        Owner = owner;
        Width = 128;
        Height = 160;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        UseLayoutRounding = true;
        var surface = (Brush)FindResource("SurfaceBrush");
        var line = (Brush)FindResource("LineBrush");
        var cards = new Grid { Width = 112, Height = 112 };
        if (entries.Count > 1)
            cards.Children.Add(new Border
            {
                Background = surface, BorderBrush = line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 4, 0, 0)
            });
        previewImage.Source = FileIconConverter.GetIcon(entries[0], large: true);
        cards.Children.Add(new Border
        {
            Background = surface, BorderBrush = line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5),
            Margin = entries.Count > 1 ? new Thickness(0, 0, 4, 4) : new Thickness(0),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 1, Opacity = 0.18 },
            Child = previewImage
        });
        if (entries.Count > 1)
            cards.Children.Add(new Border
            {
                Background = (Brush)FindResource("UpdateBrush"), CornerRadius = new CornerRadius(10),
                MinWidth = 20, Height = 20, Padding = new Thickness(5, 0, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = entries.Count.ToString(), Foreground = Brushes.White, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
        var caption = new TextBlock
        {
            Text = clipboardCopy ? "松开鼠标复制" : entries.Count > 1 ? $"复制 {entries.Count} 项" : entries[0].Name,
            Foreground = (Brush)FindResource("InkBrush"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Content = new StackPanel
        {
            Margin = new Thickness(8),
            Children =
            {
                cards,
                new Border
                {
                    Background = surface, CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 0), Child = caption
                }
            }
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            int style = Native.GetWindowLong(handle, -20);
            Native.SetWindowLong(handle, -20, style | 0x20 /* TRANSPARENT */ | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */);
            UpdatePosition();
        };
        pointerTimer.Tick += (_, _) =>
        {
            UpdatePosition();
            if (Native.KeyDown(0x1B) || !Native.KeyDown(0x11)) Finish(false);
            else if (!Native.KeyDown(0x01)) Finish(true);
        };
        Closed += (_, _) =>
        {
            closed = true;
            Finish(false);
            lifetime.Cancel();
        };
        ThumbnailReady = LoadThumbnailAsync(client, entries[0], lifetime.Token);
    }

    private async Task LoadThumbnailAsync(RemoteFileClient client, RemoteEntry entry, CancellationToken token)
    {
        if (!entry.IsImage) return;
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                byte[] bytes = await client.ReadImageAsync(entry.Path, token).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }, token);
            if (!token.IsCancellationRequested) previewImage.Source = bitmap;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log.Debug(ex, "读取拖动缩略图失败；路径 {RemotePath}", entry.Path); }
    }

    internal void UpdatePosition()
    {
        if (closed || handle == IntPtr.Zero || !Native.GetCursorPos(out var point)) return;
        Native.SetWindowPos(handle, new IntPtr(-1), point.X + 16, point.Y + 20, 0, 0, 0x1 | 0x10 /* NOSIZE | NOACTIVATE */);
    }

    internal Task<bool> WaitForReleaseAsync()
    {
        released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pointerTimer.Start();
        return released.Task;
    }

    private void Finish(bool copy)
    {
        pointerTimer.Stop();
        released?.TrySetResult(copy);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!closed) Close();
        lifetime.Dispose();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        internal static bool KeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
