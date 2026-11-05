using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace Transfer.Core.Remote;
public sealed class RemoteFileClient(string host, int port)
{
    public string Host => host;
    public int Port => port;
    public async Task<RemoteResponse> CallAsync(RemoteRequest request, CancellationToken ct = default)
    {
        var log = Log.ForContext("RequestId", request.RequestId).ForContext("Command", request.Command)
            .ForContext("RemotePath", request.Path).ForContext("Host", host).ForContext("Port", port);
        // 只对只读命令重试一次，避免写操作被重复执行。
        int attempts = request.Command is "list" or "text" or "status" ? 2 : 1;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeoutFor(request.Command));
                    using var socket = await Wire.ConnectAsync(host, port, timeout.Token);
                    var stream = socket.GetStream();
                    log.Information("开始远程文件操作；第 {Attempt} 次尝试", attempt);
                    await Wire.WriteAsync(stream, request, timeout.Token);
                    var response = await Wire.ReadAsync<RemoteResponse>(stream, timeout.Token);
                    Ensure(response); log.Information("远程文件操作成功");
                    return response;
                }
                catch (Exception ex) when (attempt < attempts && Retryable(ex, ct))
                {
                    log.Warning(ex, "远程文件操作失败，准备重试；第 {Attempt} 次", attempt);
                    await Task.Delay(500, ct);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "远程文件操作失败");
            if (ex is SocketException)
                throw new IOException($"无法连接服务端 {host}:{port}，请确认服务已启动并且网络可达", ex);
            throw;
        }
    }
    private static TimeSpan TimeoutFor(string command) => command switch
    {
        "text" or "save" => TimeSpan.FromMinutes(2),
        "list" => TimeSpan.FromSeconds(20),
        "status" or "start" or "stop" => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(30)
    };
    private static bool Retryable(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex is IOException or SocketException or TimeoutException or OperationCanceledException;
    }
    public async Task<byte[]> ReadImageAsync(string path, CancellationToken ct = default)
    {
        var request = new RemoteRequest("image", path);
        var log = Log.ForContext("RequestId", request.RequestId).ForContext("RemotePath", path);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    using var socket = await Wire.ConnectAsync(host, port, timeout.Token);
                    var stream = socket.GetStream();
                    await Wire.WriteAsync(stream, request, timeout.Token);
                    var response = await Wire.ReadAsync<RemoteResponse>(stream, timeout.Token); Ensure(response);
                    if (response.Length < 0 || response.Length > RemoteFileStore.ImageLimit) throw new IOException("图片长度超过限制");
                    var bytes = new byte[(int)response.Length];
                    await stream.ReadExactlyAsync(bytes, timeout.Token);
                    log.Information("图片获取成功；长度 {Length}", bytes.Length);
                    return bytes;
                }
                catch (Exception ex) when (attempt == 1 && Retryable(ex, ct))
                {
                    log.Warning(ex, "图片获取失败，准备重试");
                    await Task.Delay(500, ct);
                }
            }
        }
        catch (Exception ex) { log.Error(ex, "图片读取失败"); throw; }
    }
    public async Task UploadAsync(string local, string destination, bool overwrite, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        await using var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)); input.Position = 0;
        var request = new RemoteRequest("upload", destination, Length: input.Length, Sha256: hash, Overwrite: overwrite);
        var log = Log.ForContext("RequestId", request.RequestId).ForContext("RemotePath", destination).ForContext("LocalPath", local);
        try
        {
            log.Information("上传开始；长度 {Length}，覆盖 {Overwrite}", input.Length, overwrite);
            using var socket = await Wire.ConnectAsync(host, port, token);
            var stream = socket.GetStream();
            await Wire.WriteAsync(stream, request, token);
            Ensure(await Wire.ReadAsync<RemoteResponse>(stream, token));
            var buffer = new byte[131072]; long sent = 0; int n;
            while ((n = await input.ReadAsync(buffer, token)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), token); sent += n;
                progress?.Report(input.Length == 0 ? 100 : 100d * sent / input.Length);
            }
            var final = await Wire.ReadAsync<RemoteResponse>(stream, token); Ensure(final);
            if (final.Version != hash) throw new IOException("服务端返回的上传校验值不一致");
            progress?.Report(100); log.Information("上传完成且服务端校验一致");
        }
        catch (Exception ex) { log.Error(ex, "上传失败"); throw; }
    }
    public async Task<string> CompressAsync(string path, IProgress<RemoteTransferProgress>? progress = null, CancellationToken ct = default)
    {
        var request = new RemoteRequest("compress", path);
        var log = Log.ForContext("RequestId", request.RequestId).ForContext("RemotePath", path);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromHours(24));
            using var socket = await Wire.ConnectAsync(host, port, timeout.Token);
            var stream = socket.GetStream();
            log.Information("请求服务端压缩文件或文件夹");
            await Wire.WriteAsync(stream, request, timeout.Token);
            while (true)
            {
                var response = await Wire.ReadAsync<RemoteResponse>(stream, timeout.Token); Ensure(response);
                log.Debug("服务端压缩进度；阶段 {Stage}，进度 {Percent:F1}，字节 {Completed}/{Total}", response.Stage, response.Percent, response.CompletedBytes, response.TotalBytes);
                progress?.Report(new(request.RequestId, path, response.Stage ?? "压缩", response.Percent, response.CompletedBytes, response.TotalBytes));
                if (response.Stage == "完成")
                {
                    string result = response.ArchivePath ?? throw new IOException("服务端未返回 ZIP 路径");
                    log.Information("服务端压缩完成；ZIP {ArchivePath}", result); return result;
                }
            }
        }
        catch (Exception ex) { log.Error(ex, "服务端压缩失败或中断"); throw; }
    }
    public Task<RemoteResponse> DeleteFileAsync(RemoteEntry entry, CancellationToken ct = default)
    {
        if (entry.IsDirectory) throw new IOException("仅支持删除文件");
        return CallAsync(new("delete", entry.Path, Version: entry.ModifiedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), Length: entry.Length), ct);
    }
    public async Task DownloadZipAsync(string path, string destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken ct = default)
    {
        var request = new RemoteRequest("download", path);
        destination = Path.GetFullPath(destination);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        var log = Log.ForContext("RequestId", request.RequestId).ForContext("RemotePath", path).ForContext("LocalPath", destination);
        try
        {
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("本地目标已存在，请更换保存名称");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromHours(24));
            var token = timeout.Token;
            using var socket = await Wire.ConnectAsync(host, port, token);
            var stream = socket.GetStream();
            log.Information("开始下载 ZIP；临时文件 {TemporaryPath}", temp);
            await Wire.WriteAsync(stream, request, token);
            RemoteResponse header;
            do
            {
                header = await Wire.ReadAsync<RemoteResponse>(stream, token); Ensure(header);
                log.Debug("ZIP 下载准备；阶段 {Stage}，进度 {Percent:F1}", header.Stage, header.Percent);
                progress?.Report(new(request.RequestId, path, header.Stage ?? "准备下载", header.Percent, header.CompletedBytes, header.TotalBytes));
            } while (header.Stage != "接收数据");
            if (header.Length < 0 || header.Version is not { Length: 64 }) throw new IOException("服务端返回的 ZIP 长度或校验值无效");
            string actual;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long received = 0; byte[] buffer = new byte[131072];
                var timer = System.Diagnostics.Stopwatch.StartNew();
                progress?.Report(new(request.RequestId, path, "下载", 0, 0, header.Length));
                while (received < header.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, header.Length - received)), token);
                    if (n == 0) throw new EndOfStreamException("ZIP 下载连接中断");
                    await output.WriteAsync(buffer.AsMemory(0, n), token); hash.AppendData(buffer, 0, n); received += n;
                    if (timer.ElapsedMilliseconds >= 100)
                    {
                        timer.Restart(); double percent = header.Length == 0 ? 99 : 99d * received / header.Length;
                        progress?.Report(new(request.RequestId, path, "下载", percent, received, header.Length));
                        log.Debug("ZIP 下载进度；已接收 {Received}/{Length}", received, header.Length);
                    }
                }
                await output.FlushAsync(token); output.Flush(true);
                actual = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (!actual.Equals(header.Version, StringComparison.OrdinalIgnoreCase)) throw new IOException("ZIP SHA-256 校验失败，未提交本地文件");
            token.ThrowIfCancellationRequested();
            File.Move(temp, destination);
            // 本地文件已提交，回执失败单独记录，避免用户误以为已经落盘的文件需要重下。
            try { await Wire.WriteAsync(stream, new RemoteResponse(true, Version: actual), token); }
            catch (Exception ex) { log.Warning(ex, "ZIP 已保存成功，但服务端未收到回执"); }
            progress?.Report(new(request.RequestId, path, "下载完成", 100, header.Length, header.Length));
            log.Information("ZIP 下载、校验和落盘完成；长度 {Length}，SHA256 {Sha256}", header.Length, actual);
        }
        catch (Exception ex) { log.Error(ex, "ZIP 下载失败或中断"); throw; }
        finally { if (File.Exists(temp)) { File.Delete(temp); log.Debug("清理 ZIP 下载临时文件；路径 {TemporaryPath}", temp); } }
    }
    private static void Ensure(RemoteResponse response) { if (!response.Ok) throw new IOException(response.Error ?? "远程操作失败"); }
}
