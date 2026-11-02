using Serilog;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Transfer.Core;
public sealed class FileServer : IAsyncDisposable
{
    private sealed record Entry(string Path, SharedFile File);
    private readonly ConcurrentDictionary<string, Entry> files = new();
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private readonly ConcurrentDictionary<string, string> folderRoots = new();
    private readonly ConcurrentDictionary<string, string> folderOwners = new();
    private readonly ConcurrentDictionary<string, byte> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim folderGate = new(1);
    public string[] SharedFolderPaths => folderRoots.Values.ToArray();
    public string[] ExcludedPaths => excluded.Keys.ToArray();
    public void RestoreExclusions(IEnumerable<string> paths) { foreach (var path in paths) excluded[Path.GetFullPath(path)] = 0; }
    private readonly SemaphoreSlim ranges = new(64);
    private CancellationTokenSource? stop;
    private readonly object lifecycle = new();
    private Task? stopping;
    private bool disposed;
    private TcpListener? listener;
    private Task? acceptTask;
    private int sequence;
    public event Action<TransferProgress>? Progress;
    public event Action<string>? Log;
    public event Action? FilesChanged;
    public SharedFile[] Files => files.Values.Select(x => x.File).OrderBy(x => x.Name).ToArray();
    public string GetSourcePath(string fileId) => files.TryGetValue(fileId, out var entry) ? entry.Path : "";
    public bool RemoveFile(string fileId)
    {
        folderGate.Wait();
        try
        {
        if (!files.TryRemove(fileId, out var entry))
        {
            Serilog.Log.Warning("移除共享文件时记录不存在；文件编号 {FileId}", fileId);
            return false;
        }
        if (entry.File.IsDirectory)
        {
            bool isRoot = folderRoots.TryRemove(fileId, out _);
            if (!isRoot) excluded[entry.Path] = 0;
            folderOwners.TryRemove(fileId, out _);
            foreach (var path in excluded.Keys.Where(p => p.StartsWith(entry.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) excluded.TryRemove(path, out _);
            foreach (var child in folderOwners.Where(p => p.Value == fileId || files.TryGetValue(p.Key, out var item) && item.Path.StartsWith(entry.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                files.TryRemove(child.Key, out _); folderOwners.TryRemove(child.Key, out _);
            }
        }
        else if (folderOwners.TryRemove(fileId, out _)) excluded[entry.Path] = 0;
        Serilog.Log.Information("已移除共享记录，源文件保留；文件编号 {FileId}，文件名 {FileName}，源路径 {SourcePath}", fileId, entry.File.Name, entry.Path);
        return true;
        }
        finally { folderGate.Release(); }
    }
    private bool IsExcluded(string path) => excluded.Keys.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public async Task<SharedFile> AddFileAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        await folderGate.WaitAsync(ct);
        try
        {
            var file = await DescribeAsync(path, null, ct);
            foreach (var old in files.Where(p => p.Value.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            { files.TryRemove(old.Key, out _); folderOwners.TryRemove(old.Key, out _); }
            excluded.TryRemove(path, out _);
            files[file.Id] = new(path, file);
            return file;
        }
        finally { folderGate.Release(); }
    }
    private static async Task<SharedFile> DescribeAsync(string path, string? relative, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        Serilog.Log.Information("开始计算共享文件 SHA-256；源路径 {SourcePath}，相对路径 {RelativePath}", path, relative);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, true);
        long ticks = File.GetLastWriteTimeUtc(path).Ticks;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        string name = Path.GetFileName(path);
        string nameHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative ?? name)));
        if (relative == null) nameHash = nameHash[..16];
        var file = new SharedFile(hash + "-" + nameHash, name, stream.Length, hash, ticks, relative);
        Serilog.Log.Information("共享文件已就绪；文件 {FileName}，编号 {FileId}，长度 {FileLength}，SHA256 {Sha256}，耗时 {ElapsedMs} ms", name, file.Id, file.Length, hash, elapsed.ElapsedMilliseconds);
        return file;
    }
    public async Task AddFolderAsync(string path, CancellationToken ct = default)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不共享链接目录");
        string label = Path.GetFileName(path);
        if (string.IsNullOrEmpty(label)) throw new IOException("请选择具体文件夹，不能直接共享磁盘根目录");
        string id = "folder-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        await folderGate.WaitAsync(ct);
        try
        {
            if (folderRoots.Any(p => p.Key != id && Path.GetFileName(p.Value).Equals(label, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("已共享同名文件夹，请先移除或修改文件夹名称");
            if (folderRoots.Any(p => p.Key != id && (path.StartsWith(p.Value + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || p.Value.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("不能重复共享已有共享目录的父目录或子目录");
            folderRoots[id] = path;
            files[id] = new(path, new(id, label, 0, "", Directory.GetLastWriteTimeUtc(path).Ticks, label, true));
        }
        finally { folderGate.Release(); }
        await RefreshFoldersAsync(ct);
        Serilog.Log.Information("共享文件夹已添加；目录 {SourcePath}", path);
    }
    public async Task RefreshFoldersAsync(CancellationToken ct = default)
    {
        if (folderRoots.IsEmpty) return;
        await folderGate.WaitAsync(ct);
        try
        {
            foreach (var root in folderRoots.ToArray())
            {
                var seen = new HashSet<string>();
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
                if (!Directory.Exists(root.Value))
                {
                    foreach (var old in folderOwners.Where(p => p.Value == root.Key)) { files.TryRemove(old.Key, out _); folderOwners.TryRemove(old.Key, out _); }
                    continue;
                }
                foreach (var path in Directory.EnumerateDirectories(root.Value, "*", options))
                {
                    if (IsExcluded(path)) continue;
                    string relative = Path.Combine(Path.GetFileName(root.Value), Path.GetRelativePath(root.Value, path));
                    string directoryId = root.Key + "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative)));
                    files[directoryId] = new(path, new(directoryId, Path.GetFileName(path), 0, "", Directory.GetLastWriteTimeUtc(path).Ticks, relative, true));
                    folderOwners[directoryId] = root.Key; seen.Add(directoryId);
                }
                foreach (var path in Directory.EnumerateFiles(root.Value, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsExcluded(path)) continue;
                    string relative = Path.Combine(Path.GetFileName(root.Value), Path.GetRelativePath(root.Value, path));
                    var info = new FileInfo(path);
                    var existing = files.FirstOrDefault(p => p.Value.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.Value.File.RelativePath == relative);
                    SharedFile shared = existing.Value != null && existing.Value.File.Length == info.Length && existing.Value.File.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                        ? existing.Value.File : await DescribeAsync(path, relative, ct);
                    foreach (var old in files.Where(p => p.Value.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.Key != shared.Id))
                    { files.TryRemove(old.Key, out _); folderOwners.TryRemove(old.Key, out _); }
                    files[shared.Id] = new(path, shared);
                    folderOwners[shared.Id] = root.Key; seen.Add(shared.Id);
                }
                foreach (var old in folderOwners.Where(p => p.Value == root.Key && !seen.Contains(p.Key)))
                { files.TryRemove(old.Key, out _); folderOwners.TryRemove(old.Key, out _); }
            }
        }
        finally { folderGate.Release(); }
        FilesChanged?.Invoke();
    }
    public void Start(int port)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopping is { IsCompleted: false }) throw new InvalidOperationException("服务正在停止，请稍候");
            if (listener != null) throw new InvalidOperationException("服务已经启动");
            Serilog.Log.Information("准备启动 TCP 服务；监听地址 {ListenAddress}，请求端口 {Port}", "0.0.0.0", port);
            var next = new TcpListener(IPAddress.Any, port);
            try { next.Start(); }
            catch { next.Stop(); throw; }
            var cancellation = new CancellationTokenSource();
            stop = cancellation;
            listener = next;
            Serilog.Log.Information("TCP 服务启动成功；监听端点 {ListenEndpoint}", next.LocalEndpoint);
            acceptTask = Task.Run(() => AcceptAsync(next, cancellation.Token));
        }
    }
    public bool IsRunning { get { lock (lifecycle) return listener != null && stopping is not { IsCompleted: false }; } }
    private async Task AcceptAsync(TcpListener currentListener, CancellationToken stopToken)
    {
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                var client = await currentListener.AcceptTcpClientAsync(stopToken);
                int id = Interlocked.Increment(ref sequence);
                Serilog.Log.Debug("接收客户端连接；连接编号 {ConnectionId}，远端 {RemoteEndpoint}，当前活动连接 {ActiveConnections}", id, client.Client.RemoteEndPoint, connections.Count + 1);
                var task = HandleAsync(client, id, stopToken);
                connections[id] = task;
                _ = task.ContinueWith(t => { connections.TryRemove(id, out _); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { Serilog.Log.Debug("TCP 接收循环已取消"); }
        catch (SocketException) when (stopToken.IsCancellationRequested) { Serilog.Log.Debug("监听套接字随服务停止而关闭"); }
        catch (Exception ex) { Serilog.Log.Error(ex, "TCP 监听循环异常退出"); throw; }
    }
    private async Task HandleAsync(TcpClient client, int connectionId, CancellationToken stopToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            var stream = client.GetStream();
            bool binaryStarted = false;
            long sent = 0;
            var elapsed = Stopwatch.StartNew();
            var log = Serilog.Log.ForContext("ConnectionId", connectionId).ForContext("RemoteEndpoint", client.Client.RemoteEndPoint?.ToString());
            try
            {
                var request = await Wire.ReadAsync<Request>(stream, ct);
                log = log.ForContext("RequestId", request.RequestId).ForContext("Command", request.Command)
                    .ForContext("FileId", request.FileId).ForContext("Session", request.Session);
                log.Debug("收到请求；偏移 {Offset}，长度 {Count}，进度 {Completed}，状态 {State}", request.Offset, request.Count, request.Completed, request.State);
                if (request.Command == "list")
                {
                    await RefreshFoldersAsync(ct);
                    var listing = Files;
                    log.Information("返回共享文件列表；文件数 {FileCount}", listing.Length);
                    await Wire.WriteAsync(stream, new Response(true, Files: listing), ct);
                    return;
                }
                if (request.FileId == null || !files.TryGetValue(request.FileId, out var entry))
                    throw new IOException("文件未共享或已移除，请刷新列表");
                if (request.Command == "progress")
                {
                    if (request.State == "下载中") log.Debug("收到客户端进度；已写入 {Completed}，总长度 {Total}，状态 {State}", request.Completed, entry.File.Length, request.State);
                    else log.Information("收到客户端状态；已写入 {Completed}，总长度 {Total}，状态 {State}", request.Completed, entry.File.Length, request.State);
                    Progress?.Invoke(new(entry.File.Id, entry.File.Name, request.Session ?? "未知客户端",
                        Math.Clamp(request.Completed, 0, entry.File.Length), entry.File.Length, request.State ?? "下载中"));
                    await Wire.WriteAsync(stream, new Response(true), ct);
                    return;
                }
                if (request.Command != "range") throw new IOException("未知命令");
                if (request.Offset < 0 || request.Count <= 0 || request.Count > 8 * 1024 * 1024 ||
                    request.Offset > entry.File.Length - request.Count) throw new IOException("分块范围无效");
                log = log.ForContext("SourcePath", entry.Path).ForContext("FileName", entry.File.Name)
                    .ForContext("Offset", request.Offset).ForContext("Count", request.Count);
                log.Debug("分块等待发送许可");
                await ranges.WaitAsync(ct);
                try
                {
                    await using var file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    if (file.Length != entry.File.Length || File.GetLastWriteTimeUtc(entry.Path).Ticks != entry.File.ModifiedTicks)
                        throw new IOException("源文件已修改，请在服务端重新添加文件并刷新列表");
                    log.Debug("分块开始发送");
                    file.Position = request.Offset;
                    await Wire.WriteAsync(stream, new Response(true, Count: request.Count), ct);
                    binaryStarted = true;
                    var buffer = new byte[131072];
                    long remaining = request.Count;
                    while (remaining > 0)
                    {
                        int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                        if (read == 0) throw new EndOfStreamException("源文件提前结束");
                        await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                        remaining -= read; sent += read;
                        log.Verbose("分块二进制已发送；本次字节 {Bytes}，累计 {SentBytes}，剩余 {Remaining}", read, sent, remaining);
                    }
                }
                finally { ranges.Release(); }
                log.Debug("分块发送完成；字节数 {SentBytes}，耗时 {ElapsedMs} ms", sent, elapsed.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                if (stopToken.IsCancellationRequested) log.Debug(ex, "连接因服务停止而结束；已发送 {SentBytes}", sent);
                else
                {
                    log.Error(ex, "处理 TCP 请求失败；已开始发送二进制 {BinaryStarted}，已发送 {SentBytes}，是否超时 {TimedOut}，耗时 {ElapsedMs} ms", binaryStarted, sent, timeout.IsCancellationRequested, elapsed.ElapsedMilliseconds);
                    Log?.Invoke(ex.Message);
                }
                if (!binaryStarted)
                    try { await Wire.WriteAsync(stream, new Response(false, ex.Message), ct); }
                    catch (Exception replyError) { log.Debug(replyError, "无法向客户端返回错误响应"); }
            }
            finally { log.Debug("客户端连接结束；耗时 {ElapsedMs} ms", elapsed.ElapsedMilliseconds); }
        }
    }
    public Task StopAsync()
    {
        lock (lifecycle)
        {
            if (stopping is { IsCompleted: false }) return stopping;
            if (listener == null) return Task.CompletedTask;
            var currentListener = listener;
            var currentStop = stop!;
            var currentAccept = acceptTask!;
            // 后台清理避免同步完成时在赋值 stopping 之前重新进入生命周期操作。
            stopping = Task.Run(async () =>
            {
                Serilog.Log.Information("开始停止 TCP 服务；活动连接数 {ActiveConnections}", connections.Count);
                try
                {
                    currentStop.Cancel();
                    currentListener.Stop();
                    try { await currentAccept; }
                    finally { await Task.WhenAll(connections.Values.ToArray()); }
                }
                finally
                {
                    lock (lifecycle)
                    {
                        connections.Clear();
                        listener = null; stop = null; acceptTask = null;
                        currentStop.Dispose();
                    }
                    Serilog.Log.Information("TCP 服务已停止；共享列表保留，可重新启动");
                }
            });
            return stopping;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
        }
        try { await StopAsync(); }
        finally { ranges.Dispose(); }
        Serilog.Log.Information("TCP 服务对象资源已释放");
    }
}
