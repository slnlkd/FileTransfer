using Serilog;
using Serilog.Context;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Transfer.Core;
public sealed class Downloader
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private sealed class Checkpoint
    {
        public string FileId { get; set; } = "";
        public long Length { get; set; }
        public int BlockSize { get; set; } = ChunkSize;
        public long[] Offsets { get; set; } = [];
    }
    public static async Task<SharedFile[]> ListAsync(string host, int port, CancellationToken ct = default)
    {
        Log.Information("开始获取服务端共享列表；地址 {Host}，端口 {Port}", host, port);
        try
        {
            var files = (await Wire.CallAsync(host, port, new("list"), ct)).Files ?? [];
            Log.Information("获取共享列表成功；地址 {Host}，端口 {Port}，文件数 {FileCount}", host, port, files.Length);
            return files;
        }
        catch (Exception ex) { Log.Error(ex, "获取共享列表失败；地址 {Host}，端口 {Port}", host, port); throw; }
    }
    public async Task DownloadAsync(string host, int port, SharedFile file, string folder, int parallelism,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        string session = Environment.MachineName + "/" + Guid.NewGuid().ToString("N");
        using var sessionContext = LogContext.PushProperty("Session", session);
        using var fileContext = LogContext.PushProperty("FileId", file.Id);
        using var nameContext = LogContext.PushProperty("FileName", file.Name);
        using var hostContext = LogContext.PushProperty("Host", host);
        using var portContext = LogContext.PushProperty("Port", port);
        using var folderContext = LogContext.PushProperty("DownloadFolder", folder);
        var elapsed = Stopwatch.StartNew();
        Log.Information("下载任务开始；总长度 {FileLength}，并发数 {Parallelism}，期望 SHA256 {ExpectedSha256}", file.Length, parallelism, file.Sha256);
        try
        {
            await DownloadCoreAsync(host, port, file, folder, parallelism, session, progress, ct);
            Log.Information("下载任务成功结束；耗时 {ElapsedMs} ms", elapsed.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Information("下载任务已按用户请求取消；耗时 {ElapsedMs} ms", elapsed.ElapsedMilliseconds); throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载任务失败；耗时 {ElapsedMs} ms，请通过 Session 关联服务端与客户端日志", elapsed.ElapsedMilliseconds); throw;
        }
    }
    public static string GetDestinationPath(SharedFile file, string folder)
    {
        if (Path.GetFileName(file.Name) != file.Name || string.IsNullOrWhiteSpace(file.Name) || file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("服务端文件名无效");
        string relative = file.RelativePath ?? file.Name;
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or "." || p.Contains(':')))
            throw new IOException("文件相对路径无效");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载路径超出所选目录");
        return destination;
    }
    private async Task DownloadCoreAsync(string host, int port, SharedFile file, string folder, int parallelism,
        string session, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (parallelism is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(parallelism));
        if (file.IsDirectory) throw new IOException("请选择文件夹内的文件下载");
        string destination = GetDestinationPath(file, folder);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string part = destination + "." + file.Sha256[..16] + ".part";
        string statePath = part + ".json";
        // 独占锁阻止多个下载任务或进程同时修改同一个断点文件。
        Log.Debug("准备获取断点独占锁；锁路径 {LockPath}", part + ".lock");
        await using var guard = new FileStream(part + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        Log.Debug("已获取断点独占锁；二进制路径 {PartPath}，断点路径 {CheckpointPath}", part, statePath);
        int count = checked((int)((file.Length + ChunkSize - 1) / ChunkSize));
        Checkpoint state;
        bool resume = File.Exists(part) && File.Exists(statePath);
        Log.Information("检查本地断点；可续传 {CanResume}，二进制存在 {PartExists}，断点存在 {CheckpointExists}，分块数 {ChunkCount}", resume, File.Exists(part), File.Exists(statePath), count);
        if (!resume && (File.Exists(part) || File.Exists(statePath))) Log.Warning("临时文件或断点记录缺失，将从头下载");
        if (resume)
        {
            state = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(statePath, ct))
                ?? throw new IOException("断点记录无效");
            if (state.FileId != file.Id || state.Length != file.Length || state.BlockSize != ChunkSize || state.Offsets.Length != count)
                throw new IOException("断点记录与文件不匹配，请移走临时文件后重试");
            if (new FileInfo(part).Length != file.Length) throw new IOException("临时文件长度不匹配");
            for (int i = 0; i < count; i++)
                if (state.Offsets[i] < 0 || state.Offsets[i] > Math.Min(ChunkSize, file.Length - (long)i * ChunkSize))
                    throw new IOException("断点记录中的分块进度无效");
        }
        else state = new() { FileId = file.Id, Length = file.Length, Offsets = new long[count] };
        await using var output = new FileStream(part, resume ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite,
            FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        output.SetLength(file.Length);
        Log.Debug("临时文件已打开并设置长度；路径 {PartPath}，长度 {FileLength}", part, file.Length);
        using var gate = new SemaphoreSlim(1, 1);
        long completed = state.Offsets.Sum();
        Log.Information("断点加载完成；已下载 {Completed}，总长度 {Total}，续传 {IsResume}", completed, file.Length, resume);
        int reportFailures = 0;
        async Task SaveAsync()
        {
            // 数据落盘后才原子替换断点记录，断点不会超前于磁盘中的二进制数据。
            try
            {
                output.Flush(true);
                await File.WriteAllTextAsync(statePath + ".tmp", JsonSerializer.Serialize(state));
                File.Move(statePath + ".tmp", statePath, true);
                Log.Debug("断点已持久化；路径 {CheckpointPath}，已下载 {Completed}，总长度 {Total}", statePath, state.Offsets.Sum(), file.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存断点失败；二进制路径 {PartPath}，断点路径 {CheckpointPath}，内存已下载 {Completed}", part, statePath, state.Offsets.Sum());
                throw;
            }
        }
        async Task ReportAsync(string status)
        {
            long done = Interlocked.Read(ref completed);
            progress?.Report(new(file.Id, file.Name, session, done, file.Length, status));
            if (status == "下载中") Log.Debug("本地下载进度；已写入 {Completed}，总长度 {Total}", done, file.Length);
            else Log.Information("本地下载状态；状态 {State}，已写入 {Completed}，总长度 {Total}", status, done, file.Length);
            try
            {
                await Wire.CallAsync(host, port, new("progress", file.Id, Session: session, Completed: done, State: status), CancellationToken.None);
                if (reportFailures > 0) Log.Information("进度上报连接恢复；此前连续失败 {FailureCount} 次", reportFailures);
                reportFailures = 0;
            }
            catch (Exception ex)
            {
                reportFailures++;
                if (reportFailures == 1 || status != "下载中") Log.Warning(ex, "向服务端上报进度失败，本地断点不受影响；状态 {State}，已写入 {Completed}，连续失败 {FailureCount}", status, done, reportFailures);
                else Log.Debug(ex, "进度上报持续失败；连续失败 {FailureCount}", reportFailures);
            }
        }
        using var workersStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reporterStop = new CancellationTokenSource();
        Task reporter = Task.CompletedTask;
        bool outputClosed = false;
        try
        {
            await SaveAsync();
            reporter = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await ReportAsync("下载中");
                        await Task.Delay(700, reporterStop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
            var pending = new ConcurrentQueue<int>(Enumerable.Range(0, count).Where(i => state.Offsets[i] < Math.Min(ChunkSize, file.Length - (long)i * ChunkSize)));
            async Task Worker()
            {
                try
                {
                    while (pending.TryDequeue(out int i))
                    {
                        using var chunkContext = LogContext.PushProperty("ChunkIndex", i);
                        string? blockRequestId = null;
                        long blockOffset = (long)i * ChunkSize + state.Offsets[i];
                        try
                        {
                            var chunkElapsed = Stopwatch.StartNew();
                            var token = workersStop.Token;
                            long start = (long)i * ChunkSize;
                            long remaining = Math.Min(ChunkSize, file.Length - start) - state.Offsets[i];
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(60));
                            var request = new Request("range", file.Id, start + state.Offsets[i], remaining, Session: session);
                            blockRequestId = request.RequestId;
                            using var requestContext = LogContext.PushProperty("RequestId", request.RequestId);
                            using var offsetContext = LogContext.PushProperty("Offset", request.Offset);
                            using var countContext = LogContext.PushProperty("Count", request.Count);
                            Log.Debug("分块开始下载；块内已有字节 {ChunkCompleted}，请求偏移 {Offset}，请求长度 {Count}", state.Offsets[i], request.Offset, request.Count);
                            using var client = await Wire.ConnectAsync(host, port, timeout.Token);
                            var stream = client.GetStream();
                            await Wire.WriteAsync(stream, request, timeout.Token);
                            var response = await Wire.ReadAsync<Response>(stream, timeout.Token);
                            if (!response.Ok || response.Count != remaining) throw new IOException(response.Error ?? "服务端分块长度不匹配");
                            var buffer = new byte[131072];
                            long sinceSave = 0;
                            while (remaining > 0)
                            {
                                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token);
                                if (read == 0) throw new EndOfStreamException("网络连接中断，已保留下载进度");
                                await gate.WaitAsync(token);
                                try
                                {
                                    await RandomAccess.WriteAsync(output.SafeFileHandle, buffer.AsMemory(0, read), start + state.Offsets[i], token);
                                    Log.Verbose("二进制数据已写入；块编号 {ChunkIndex}，绝对偏移 {WriteOffset}，本次字节 {Bytes}", i, start + state.Offsets[i], read);
                                    state.Offsets[i] += read;
                                    Interlocked.Add(ref completed, read);
                                    progress?.Report(new(file.Id, file.Name, session, Interlocked.Read(ref completed), file.Length, "下载中"));
                                    sinceSave += read;
                                    if (sinceSave >= 1048576) { await SaveAsync(); sinceSave = 0; }
                                }
                                finally { gate.Release(); }
                                remaining -= read;
                            }
                            await gate.WaitAsync(token);
                            try { await SaveAsync(); } finally { gate.Release(); }
                            Log.Debug("分块下载完成；块编号 {ChunkIndex}，块字节数 {ChunkBytes}，耗时 {ElapsedMs} ms", i, state.Offsets[i], chunkElapsed.ElapsedMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            var blockLog = Log.ForContext("RequestId", blockRequestId).ForContext("Offset", blockOffset);
                            if (workersStop.IsCancellationRequested) blockLog.Debug(ex, "分块下载取消；块内已完成 {ChunkCompleted}", state.Offsets[i]);
                            else blockLog.Error(ex, "分块下载失败；块内已完成 {ChunkCompleted}，块起点 {ChunkStart}，请求超时上限 {TimeoutSeconds} 秒", state.Offsets[i], (long)i * ChunkSize, 60);
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (workersStop.IsCancellationRequested) Log.Debug(ex, "分块工作线程收到停止信号");
                    else Log.Error(ex, "分块工作线程异常，将取消同一文件的其余工作线程");
                    workersStop.Cancel(); throw;
                }
            }
            await Task.WhenAll(Enumerable.Range(0, parallelism).Select(_ => Worker()));
            ct.ThrowIfCancellationRequested();
            reporterStop.Cancel(); await reporter;
            await ReportAsync("校验中");
            Log.Information("开始校验临时文件 SHA-256；路径 {PartPath}，期望校验值 {ExpectedSha256}", part, file.Sha256);
            output.Position = 0;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(output, ct));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("SHA-256 校验不一致，重置断点；期望 {ExpectedSha256}，实际 {ActualSha256}，路径 {PartPath}", file.Sha256, hash, part);
                Array.Clear(state.Offsets); Interlocked.Exchange(ref completed, 0);
                await SaveAsync();
                throw new IOException("SHA-256 校验失败，断点已重置；再次下载将重新获取全部分块");
            }
            Log.Information("SHA-256 校验通过；实际校验值 {ActualSha256}", hash);
            await output.DisposeAsync();
            outputClosed = true;
            Log.Information("准备将临时文件改名；源 {PartPath}，目标 {DestinationPath}，目标已存在 {DestinationExists}", part, destination, File.Exists(destination));
            File.Move(part, destination, false);
            File.Delete(statePath);
            File.Delete(statePath + ".tmp");
            // 锁文件随独占句柄关闭自动删除，避免先释放锁再删除导致误删另一进程的锁。
            await guard.DisposeAsync();
            Log.Information("正式文件已保存，当前任务的二进制临时文件、断点、断点临时记录和锁文件均已清理；目标 {DestinationPath}，断点 {CheckpointPath}", destination, statePath);
            await ReportAsync("已完成");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "下载结束前保存异常现场；主动取消 {Cancelled}，已写入 {Completed}，临时文件 {PartPath}", ct.IsCancellationRequested, completed, part);
            reporterStop.Cancel(); await reporter;
            if (!outputClosed)
            {
                try { await SaveAsync(); }
                catch (Exception saveError) { Log.Error(saveError, "异常退出时保存断点失败，将保留原始下载异常；原异常 {OriginalError}，断点 {CheckpointPath}", ex.Message, statePath); }
            }
            await ReportAsync(ct.IsCancellationRequested ? "已暂停" : "已中断");
            throw;
        }
    }
}
