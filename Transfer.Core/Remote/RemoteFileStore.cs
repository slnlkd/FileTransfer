using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Transfer.Core.Remote;

public sealed class RemoteFileStore
{
    public const int TextLimit = 10 * 1024 * 1024;
    public const long ImageLimit = 64 * 1024 * 1024;
    public string Root { get; }
    public SemaphoreSlim Mutation { get; } = new(1);
    public RemoteFileStore(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        CheckLink(Root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
    // 只接受相对共享根目录的路径；拒绝目录穿越、绝对路径、NTFS 数据流和链接跳转。
    public string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("只能操作共享目录内的相对路径");
        var segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("路径包含无效片段");
        string full = Path.GetFullPath(Path.Combine(Root, Path.Combine(new[] { "." }.Concat(segments).ToArray())));
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("路径超出共享根目录");
        CheckLink(Root);
        string current = Root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current)) CheckLink(current);
        }
        return full;
    }
    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不允许访问链接或重解析点：" + path);
    }
    public RemoteEntry[] List(string relative)
    {
        string directory = Resolve(relative);
        return Directory.EnumerateFileSystemEntries(directory).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
            .Where(p => !Path.GetFileName(p).StartsWith(".remote-upload-", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).StartsWith(".remote-archive-", StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                bool dir = Directory.Exists(p);
                var info = new FileInfo(p);
                return new RemoteEntry(Path.GetFileName(p), Path.GetRelativePath(Root, p).Replace('\\', '/'), dir,
                    dir ? 0 : info.Length, File.GetCreationTimeUtc(p), File.GetLastWriteTimeUtc(p));
            }).OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, NaturalComparer.Instance).ToArray();
    }
    private static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".log" or ".json";
    private static Encoding DecodeEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new UTF8Encoding(true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return new UTF32Encoding(false, true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return new UTF32Encoding(true, true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, true, true);
        try { new UTF8Encoding(false, true).GetString(bytes); return new UTF8Encoding(false, true); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
    }
    private static string EncodingId(Encoding enc) => enc.CodePage + ":" + (enc.GetPreamble().Length > 0 ? "bom" : "plain");
    private static Encoding FromId(string? id) => id switch
    {
        "65001:bom" => new UTF8Encoding(true, true), "65001:plain" => new UTF8Encoding(false, true),
        "1200:bom" => new UnicodeEncoding(false, true, true), "1201:bom" => new UnicodeEncoding(true, true, true),
        "12000:bom" => new UTF32Encoding(false, true, true), "12001:bom" => new UTF32Encoding(true, true, true),
        "936:plain" => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new IOException("不支持的文本编码")
    };
    private static async Task<byte[]> ReadTextBytesAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        if (input.Length > TextLimit) throw new IOException($"文本预览最多支持 10 MiB，当前文件 {input.Length / 1048576d:F1} MiB，请缩小文件后查看");
        using var memory = new MemoryStream();
        var buffer = new byte[65536];
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + n > TextLimit) throw new IOException("文本增长超过预览上限");
            memory.Write(buffer, 0, n);
        }
        return memory.ToArray();
    }
    public async Task<RemoteResponse> ReadTextAsync(string relative, CancellationToken ct)
    {
        string path = Resolve(relative);
        if (!IsText(path)) throw new IOException("仅支持 txt、log、json 文本查看");
        byte[] bytes = await ReadTextBytesAsync(path, ct);
        var encoding = DecodeEncoding(bytes);
        int preamble = encoding.GetPreamble().Length;
        string text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        return new(true, Text: text, Version: Convert.ToHexString(SHA256.HashData(bytes)), EncodingName: EncodingId(encoding));
    }
    public async Task<RemoteResponse> SaveTextAsync(RemoteRequest request, CancellationToken ct)
    {
        string path = Resolve(request.Path);
        if (!IsText(path)) throw new IOException("仅支持 txt、log、json 文本修改");
        if (request.Version == null || request.Text == null) throw new IOException("保存文本需要原始版本和内容");
        byte[] original = await ReadTextBytesAsync(path, ct);
        if (Convert.ToHexString(SHA256.HashData(original)) != request.Version) throw new IOException("文件已被其他程序修改，请重新打开后再编辑");
        var encoding = FromId(request.EncodingName);
        byte[] body = encoding.GetPreamble().Concat(encoding.GetBytes(request.Text)).ToArray();
        if (body.Length > TextLimit) throw new IOException($"保存文本超过 10 MiB 上限（当前 {body.Length / 1048576d:F1} MiB）");
        await AtomicWriteAsync(path, body, request.Version, ct);
        Log.Information("远程文本保存成功；路径 {RemotePath}，长度 {Length}，编码 {Encoding}", request.Path, body.Length, request.EncodingName);
        return new(true, Version: Convert.ToHexString(SHA256.HashData(body)), EncodingName: request.EncodingName);
    }
    private static async Task AtomicWriteAsync(string path, byte[] bytes, string expectedVersion, CancellationToken ct)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".remote-upload-" + Guid.NewGuid().ToString("N"));
        DateTime creation = File.GetCreationTimeUtc(path);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            // 临时文件写完后再次乐观校验；替换前释放读取句柄，兼容 Windows 文件替换语义。
            if (Convert.ToHexString(SHA256.HashData(await ReadTextBytesAsync(path, ct))) != expectedVersion)
                throw new IOException("文件在保存过程中发生修改，请重新打开后再编辑");
            File.Move(temporary, path, true);
            File.SetCreationTimeUtc(path, creation);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public ProcessState Status(string relative)
    {
        string path = Resolve(relative);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new IOException("请选择共享目录中的 EXE 文件");
        var ids = new List<int>(); int inaccessible = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is string executable && Path.GetFullPath(executable).Equals(path, StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    inaccessible++; Log.Debug(ex, "读取进程完整路径失败；PID {ProcessId}，文件 {RemotePath}", process.Id, relative);
                }
            }
        }
        return new(ids.ToArray(), ids.Count > 0 ? "运行中" : inaccessible > 0 ? "状态未知" : "未运行",
            inaccessible > 0 ? $"有 {inaccessible} 个同名进程无法读取路径，请检查服务端权限" : null);
    }
    public async Task<ProcessState> StopProcessAsync(string relative, CancellationToken ct)
    {
        string expected = Resolve(relative);
        var state = Status(relative);
        if (state.Detail != null) throw new IOException(state.Detail + "；不能确认进程时不执行停止");
        foreach (int id in state.ProcessIds)
        {
            if (id == Environment.ProcessId) throw new IOException("不能通过远程操作停止文件管理服务端自身");
            try
            {
                using var process = Process.GetProcessById(id);
                if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("进程路径已变化，请刷新状态");
                Log.Warning("远程请求终止 EXE；文件 {RemotePath}，PID {ProcessId}", relative, id);
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(ct);
            }
            catch (ArgumentException) { /* 进程已自行退出。 */ }
        }
        return Status(relative);
    }
    public ProcessState StartProcess(string relative)
    {
        var state = Status(relative);
        if (state.ProcessIds.Length > 0) throw new IOException("程序已在运行，请先停止再启动");
        if (state.Detail != null) throw new IOException(state.Detail);
        string path = Resolve(relative);
        using var process = Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new IOException("进程启动失败");
        Log.Warning("远程启动 EXE；文件 {RemotePath}，PID {ProcessId}，工作目录 {WorkingDirectory}", relative, process.Id, Path.GetDirectoryName(path));
        return new(new[] { process.Id }, "已启动", "请刷新确认程序是否持续运行");
    }
}
