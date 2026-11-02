using Serilog;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Transfer.Core;
public record SharedFile(string Id, string Name, long Length, string Sha256, long ModifiedTicks, string? RelativePath = null, bool IsDirectory = false);
public record Request(string Command, string? FileId = null, long Offset = 0, long Count = 0,
    string? Session = null, long Completed = 0, string? State = null)
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
}
public record Response(bool Ok, string? Error = null, SharedFile[]? Files = null, long Count = 0);
public record TransferProgress(string FileId, string Name, string Session, long Completed, long Total, string State);

public static class Wire
{
    private const int MaxHeaderBytes = 64 * 1024 * 1024;
    // 中文按 UTF-8 直接写入协议头，避免 \uXXXX 转义把 10 MiB 文本撑到 20 MiB 以上。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        Log.Verbose("发送协议头；类型 {HeaderType}，字节数 {HeaderBytes}", typeof(T).Name, bytes.Length);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync(new byte[] { 10 }, ct);
        await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        using var header = new MemoryStream();
        var one = new byte[1];
        // 不使用带缓冲的文本读取器，避免吞掉后面的文件数据。
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("连接已关闭");
            if (one[0] == 10) break;
            if (header.Length >= MaxHeaderBytes) throw new InvalidDataException("协议头过大");
            header.WriteByte(one[0]);
        }
        Log.Verbose("接收协议头；类型 {HeaderType}，字节数 {HeaderBytes}", typeof(T).Name, header.Length);
        return JsonSerializer.Deserialize<T>(header.ToArray(), JsonOptions) ?? throw new InvalidDataException("协议数据为空");
    }
    public static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        var log = Log.ForContext("Host", host).ForContext("Port", port);
        var elapsed = Stopwatch.StartNew();
        try
        {
            log.Debug("开始建立 TCP 连接");
            await client.ConnectAsync(host, port, ct);
            log.Debug("TCP 连接成功；本地 {LocalEndpoint}，远端 {RemoteEndpoint}，耗时 {ElapsedMs} ms", client.Client.LocalEndPoint, client.Client.RemoteEndPoint, elapsed.ElapsedMilliseconds);
            return client;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "TCP 连接失败或取消；主动取消 {Cancelled}，耗时 {ElapsedMs} ms", ct.IsCancellationRequested, elapsed.ElapsedMilliseconds);
            client.Dispose(); throw;
        }
    }
    public static async Task<Response> CallAsync(string host, int port, Request request, CancellationToken ct)
    {
        var log = Log.ForContext("RequestId", request.RequestId).ForContext("Command", request.Command)
            .ForContext("FileId", request.FileId).ForContext("Session", request.Session).ForContext("Host", host).ForContext("Port", port);
        var elapsed = Stopwatch.StartNew();
        log.Debug("开始 TCP 请求；偏移 {Offset}，长度 {Count}，进度 {Completed}，状态 {State}", request.Offset, request.Count, request.Completed, request.State);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = await ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            await WriteAsync(stream, request, timeout.Token);
            var response = await ReadAsync<Response>(stream, timeout.Token);
            if (!response.Ok) throw new IOException(response.Error);
            log.Debug("TCP 请求成功；耗时 {ElapsedMs} ms，返回文件数 {FileCount}", elapsed.ElapsedMilliseconds, response.Files?.Length);
            return response;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "TCP 请求失败；主动取消 {Cancelled}，耗时 {ElapsedMs} ms", ct.IsCancellationRequested, elapsed.ElapsedMilliseconds);
            throw;
        }
    }
}
