using Serilog.Context;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace Transfer.Core.Remote;
public sealed class RemoteFileService : IAsyncDisposable
{
    private readonly object lifecycle = new();
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Task? accept;
    private Task? stopping;
    private int sequence;
    private bool disposed;
    public RemoteFileStore Store { get; }
    // 可选的目录过滤扩展；未设置时列表请求保持原样。
    public Func<string, string, RemoteEntry[], Task<RemoteFilterResult>>? ListFilter { get; set; }
    public event Action<string>? Activity;
    public event Action<RemoteTransferProgress>? TransferProgress;
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public RemoteFileService(string root) => Store = new(root);
    public void Start(int port)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listener != null || stopping is { IsCompleted: false }) throw new IOException("服务正在运行或停止");
            var next = new TcpListener(IPAddress.Any, port);
            try { next.Start(); } catch { next.Stop(); throw; }
            var source = new CancellationTokenSource();
            listener = next; cancellation = source;
            accept = Task.Run(() => AcceptAsync(next, source.Token));
            Log.Information("远程文件管理 TCP 服务启动；根目录 {Root}，端口 {Port}", Store.Root, Port);
        }
    }
    private async Task AcceptAsync(TcpListener current, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await current.AcceptTcpClientAsync(ct);
                int id = Interlocked.Increment(ref sequence);
                connections[id] = HandleAsync(client, id, ct);
                _ = connections[id].ContinueWith(t => { connections.TryRemove(id, out _); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error(ex, "远程文件管理监听异常"); throw; }
    }
    private async Task HandleAsync(TcpClient client, int id, CancellationToken stop)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var ct = timeout.Token;
            var stream = client.GetStream();
            var log = Log.ForContext("ConnectionId", id).ForContext("RemoteEndpoint", client.Client.RemoteEndPoint?.ToString());
            bool sendingBinary = false;
            RemoteRequest? active = null;
            try
            {
                var request = await Wire.ReadAsync<RemoteRequest>(stream, ct);
                active = request;
                if (request.Command is "compress" or "download") timeout.CancelAfter(TimeSpan.FromHours(24));
                using var requestContext = LogContext.PushProperty("RequestId", request.RequestId);
                using var pathContext = LogContext.PushProperty("RemotePath", request.Path);
                log = log.ForContext("RequestId", request.RequestId).ForContext("Command", request.Command).ForContext("RemotePath", request.Path);
                log.Information("远程文件请求开始；长度 {Length}，覆盖 {Overwrite}", request.Length, request.Overwrite);
                RemoteResponse response;
                switch (request.Command)
                {
                    case "list": response = await ListAsync(request); break;
                    case "text": response = await Store.ReadTextAsync(request.Path, ct); break;
                    case "image":
                        string imagePath = Store.Resolve(request.Path);
                        if (Path.GetExtension(imagePath).ToLowerInvariant() is not (".jpg" or ".png")) throw new IOException("仅支持 jpg、png 图片查看");
                        await using (var input = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                        {
                            if (input.Length > RemoteFileStore.ImageLimit) throw new IOException("图片预览不能超过 64 MiB");
                            await Wire.WriteAsync(stream, new RemoteResponse(true, Length: input.Length), ct);
                            sendingBinary = true;
                            await input.CopyToAsync(stream, ct);
                        }
                        log.Information("图片发送完成");
                        return;
                    case "status": response = new(true, Process: Store.Status(request.Path)); break;
                    case "compress":
                        await ReportAsync(stream, request, "等待压缩", 0, 0, 0, ct);
                        await Store.Mutation.WaitAsync(ct);
                        try
                        {
                            string archive = await RemoteArchives.CreateAsync(Store, request.Path,
                                (stage, percent, done, total) => ReportAsync(stream, request, stage, percent, done, total, ct), ct);
                            response = new(true, Stage: "完成", Percent: 100, ArchivePath: archive);
                        }
                        finally { Store.Mutation.Release(); }
                        TransferProgress?.Invoke(new(request.RequestId, request.Path, "压缩完成", 100));
                        break;
                    case "download":
                        string zip = Store.Resolve(request.Path);
                        if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("当前只提供 ZIP 下载");
                        await using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                        {
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            var buffer = new byte[131072]; long hashed = 0; int read;
                            var timer = System.Diagnostics.Stopwatch.StartNew();
                            await ReportAsync(stream, request, "计算校验值", 0, 0, input.Length, ct);
                            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                hash.AppendData(buffer, 0, read); hashed += read;
                                if (timer.ElapsedMilliseconds >= 200)
                                { timer.Restart(); await ReportAsync(stream, request, "计算校验值", input.Length == 0 ? 100 : 100d * hashed / input.Length, hashed, input.Length, ct); }
                            }
                            string sha256 = Convert.ToHexString(hash.GetHashAndReset()); input.Position = 0;
                            log.Information("ZIP 校验值计算完成，开始发送；长度 {Length}，SHA256 {Sha256}", input.Length, sha256);
                            await Wire.WriteAsync(stream, new RemoteResponse(true, Version: sha256, Length: input.Length, Stage: "接收数据"), ct);
                            sendingBinary = true;
                            long sent = 0;
                            TransferProgress?.Invoke(new(request.RequestId, request.Path, "发送 ZIP", 0, 0, input.Length));
                            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                await stream.WriteAsync(buffer.AsMemory(0, read), ct); sent += read;
                                if (timer.ElapsedMilliseconds >= 200)
                                {
                                    timer.Restart();
                                    log.Debug("ZIP 发送进度；已发送 {Sent}/{Length}", sent, input.Length);
                                    TransferProgress?.Invoke(new(request.RequestId, request.Path, "发送 ZIP", input.Length == 0 ? 100 : 100d * sent / input.Length, sent, input.Length));
                                }
                            }
                            // 等待客户端落盘和校验回执，服务端不把“已写入网络”误报为“下载完成”。
                            var receipt = await Wire.ReadAsync<RemoteResponse>(stream, ct);
                            if (!receipt.Ok || receipt.Version != sha256) throw new IOException("客户端未确认下载成功：" + receipt.Error);
                            TransferProgress?.Invoke(new(request.RequestId, request.Path, "下载完成", 100, sent, sent));
                            log.Information("ZIP 下载完成且收到客户端校验回执；长度 {Length}，SHA256 {Sha256}", sent, sha256);
                            Activity?.Invoke("下载完成  " + request.Path);
                        }
                        return;
                    case "save":
                    case "upload":
                    case "mkdir":
                    case "delete":
                    case "start":
                    case "stop":
                        await Store.Mutation.WaitAsync(ct);
                        try
                        {
                            response = request.Command switch
                            {
                                "save" => await Store.SaveTextAsync(request, ct),
                                "upload" => await ReceiveUploadAsync(stream, request, ct),
                                "start" => new(true, Process: Store.StartProcess(request.Path)),
                                "stop" => new(true, Process: await Store.StopProcessAsync(request.Path, ct)),
                                "delete" => DeleteFile(request),
                                _ => CreateFolder(request.Path)
                            };
                        }
                        finally { Store.Mutation.Release(); }
                        break;
                    default: throw new IOException("未知的文件管理命令");
                }
                await Wire.WriteAsync(stream, response, ct);
                log.Information("远程文件请求完成");
                Activity?.Invoke($"{request.Command}  {request.Path}");
            }
            catch (Exception ex)
            {
                if (active?.Command is "compress" or "download") TransferProgress?.Invoke(new(active.RequestId, active.Path, "失败或中断：" + ex.Message, 0));
                if (stop.IsCancellationRequested) log.Debug(ex, "服务停止，取消远程操作");
                else { log.Error(ex, "远程文件请求失败"); Activity?.Invoke("失败：" + ex.Message); }
                if (!sendingBinary)
                    try { await Wire.WriteAsync(stream, new RemoteResponse(false, ex.Message), ct); }
                    catch (Exception reply) { log.Debug(reply, "返回文件管理错误响应失败"); }
            }
        }
    }
    private async Task ReportAsync(Stream stream, RemoteRequest request, string stage, double percent, long completed, long total, CancellationToken ct)
    {
        TransferProgress?.Invoke(new(request.RequestId, request.Path, stage, percent, completed, total));
        Log.Debug("远程传输进度；阶段 {Stage}，进度 {Percent:F1}，字节 {Completed}/{Total}", stage, percent, completed, total);
        await Wire.WriteAsync(stream, new RemoteResponse(true, Stage: stage, Percent: percent, CompletedBytes: completed, TotalBytes: total), ct);
    }
    private async Task<RemoteResponse> ListAsync(RemoteRequest request)
    {
        var entries = Store.List(request.Path);
        string rootName = Path.GetFileName(Store.Root);
        if (request.Filter is null || ListFilter is null) return new(true, Entries: entries, RootName: rootName);
        try
        {
            var filtered = await ListFilter(request.Filter, request.Path, entries);
            return new(true, Entries: filtered.Entries, RootName: rootName, Warning: filtered.Warning);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "目录过滤失败，回退显示全部；过滤器 {Filter}，路径 {RemotePath}", request.Filter, request.Path);
            return new(true, Entries: entries, RootName: rootName, Warning: "目录过滤失败，已显示全部：" + ex.Message);
        }
    }
    private RemoteResponse CreateFolder(string relative)
    {
        string path = Store.Resolve(relative);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("同名文件或文件夹已存在");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new IOException("父文件夹不存在，请先刷新");
        Directory.CreateDirectory(path);
        return new(true);
    }
    private RemoteResponse DeleteFile(RemoteRequest request)
    {
        string path = Store.Resolve(request.Path);
        if (Directory.Exists(path)) throw new IOException("仅支持删除文件，不能删除文件夹或共享根目录");
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("文件不存在，请刷新列表");
        if (request.Version != file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) || request.Length != file.Length)
            throw new IOException("文件在列表加载后已发生变化，请刷新后再确认删除");
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var state = Store.Status(request.Path);
            if (state.ProcessIds.Length > 0 || state.Detail != null) throw new IOException("请先停止 EXE 并确认状态后再删除");
        }
        Log.Warning("执行服务端文件删除；路径 {RemotePath}，长度 {Length}，修改时间 {ModifiedUtc}", request.Path, file.Length, file.LastWriteTimeUtc);
        File.Delete(path);
        Log.Information("服务端文件删除成功；路径 {RemotePath}", request.Path);
        return new(true);
    }
    private async Task<RemoteResponse> ReceiveUploadAsync(Stream stream, RemoteRequest request, CancellationToken ct)
    {
        if (request.Length < 0 || request.Sha256 is not { Length: 64 }) throw new IOException("上传长度或 SHA-256 无效");
        string path = Store.Resolve(request.Path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new IOException("上传目标文件夹不存在");
        if (Directory.Exists(path)) throw new IOException("目标路径是文件夹");
        if (File.Exists(path) && !request.Overwrite) throw new IOException("目标文件已存在，需要选择覆盖");
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var state = Store.Status(request.Path);
            if (state.ProcessIds.Length > 0 || state.Detail != null) throw new IOException("上传前请停止对应 EXE 并确认运行状态");
        }
        string temp = Path.Combine(Path.GetDirectoryName(path)!, ".remote-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            string actual;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await Wire.WriteAsync(stream, new RemoteResponse(true), ct);
                long remaining = request.Length;
                var buffer = new byte[131072];
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (read == 0) throw new EndOfStreamException("上传中断，原目标文件保持不变");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); hash.AppendData(buffer, 0, read); remaining -= read;
                }
                await output.FlushAsync(ct); output.Flush(true);
                actual = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (!actual.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("上传 SHA-256 不一致，未替换目标文件");
            ct.ThrowIfCancellationRequested();
            Store.Resolve(request.Path);
            File.Move(temp, path, request.Overwrite);
            Log.Information("上传提交成功；路径 {RemotePath}，长度 {Length}，校验值 {Sha256}", request.Path, request.Length, actual);
            return new(true, Version: actual);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task StopAsync()
    {
        lock (lifecycle)
        {
            if (stopping is { IsCompleted: false }) return stopping;
            if (listener == null) return Task.CompletedTask;
            var current = listener; var source = cancellation!; var loop = accept!;
            stopping = Task.Run(async () =>
            {
                Log.Information("停止远程文件管理服务；活动请求 {Count}", connections.Count);
                try
                {
                    source.Cancel(); current.Stop();
                    try { await loop; } finally { await Task.WhenAll(connections.Values.ToArray()); }
                }
                finally { lock (lifecycle) { listener = null; accept = null; cancellation = null; connections.Clear(); source.Dispose(); } }
            });
            return stopping;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (lifecycle) { if (disposed) return; disposed = true; }
        try { await StopAsync(); } finally { Store.Mutation.Dispose(); }
    }
}
