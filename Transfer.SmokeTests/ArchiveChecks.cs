using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using Transfer.Core;
using Transfer.Core.Remote;

internal static class ArchiveChecks
{
    private sealed class Callback<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    public static async Task RunAsync(string testRoot, Action<bool, string> check)
    {
        string root = Path.Combine(testRoot, "zip-root");
        Directory.CreateDirectory(Path.Combine(root, "中文文件夹", "空目录"));
        Directory.CreateDirectory(Path.Combine(root, "中文文件夹", "子目录"));
        await File.WriteAllTextAsync(Path.Combine(root, "中文文件夹", "子目录", "内容.txt"), "ZIP 中文内容");
        byte[] content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(root, "中文文件夹", "数据.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(root, "中文文件夹", "空文件"), []);
        await File.WriteAllTextAsync(Path.Combine(root, "中文文件夹.zip"), "同名旧文件不能覆盖");
        await using var server = new RemoteFileService(root); server.Start(0);
        var client = new RemoteFileClient("127.0.0.1", server.Port);
        var serverProgress = new System.Collections.Concurrent.ConcurrentQueue<RemoteTransferProgress>();
        server.TransferProgress += serverProgress.Enqueue;
        var progress = new List<RemoteTransferProgress>();
        string zip = await client.CompressAsync("中文文件夹", new Callback<RemoteTransferProgress>(progress.Add));
        check(zip == "中文文件夹 (1).zip" && await File.ReadAllTextAsync(Path.Combine(root, "中文文件夹.zip")) == "同名旧文件不能覆盖", "压缩 ZIP 同名自动换名并保留旧文件");
        check(progress.Any(p => p.Stage == "扫描目录") && progress.Any(p => p.Stage == "压缩") && progress.Last().Percent == 100, "客户端收到扫描、压缩和完成进度");
        using (var archive = ZipFile.OpenRead(Path.Combine(root, zip)))
        {
            check(archive.GetEntry("中文文件夹/空目录/") != null && archive.GetEntry("中文文件夹/空文件")?.Length == 0, "ZIP 保留目录层级、空目录和空文件");
            using var reader = new StreamReader(archive.GetEntry("中文文件夹/子目录/内容.txt")!.Open());
            check(await reader.ReadToEndAsync() == "ZIP 中文内容", "ZIP 中文目录和文件内容正确");
        }
        check((await client.CallAsync(new("list"))).Entries!.Any(e => e.IsZip && e.Path == zip), "压缩完成后刷新可发现 ZIP");
        string destination = Path.Combine(testRoot, "下载.zip"); progress.Clear();
        await client.DownloadZipAsync(zip, destination, new Callback<RemoteTransferProgress>(progress.Add));
        check((await File.ReadAllBytesAsync(destination)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(root, zip))), "ZIP TCP 下载后字节和 SHA-256 一致");
        check(progress.Any(p => p.Stage == "计算校验值") && progress.Any(p => p.Stage == "下载") && progress.Last().Percent == 100, "ZIP 下载准备、接收和校验完成阶段有进度");
        await WaitAsync(() => serverProgress.Any(p => p.Stage == "下载完成"));
        check(serverProgress.Any(p => p.Stage == "压缩完成") && serverProgress.Any(p => p.Stage == "发送 ZIP"), "服务端记录压缩、发送及下载回执进度");
        check(!Directory.GetFiles(testRoot, "*.part").Any() && !Directory.GetFiles(root, ".remote-archive-*", SearchOption.AllDirectories).Any(), "成功后无下载或压缩临时文件残留");
        await RejectAsync(() => client.DownloadZipAsync(zip, destination), check, "ZIP 下载不覆盖本地已有文件");
        await RejectAsync(() => client.CompressAsync("../越界"), check, "压缩拒绝目录越界");
        await RejectAsync(() => client.CompressAsync("不存在的文件"), check, "压缩拒绝不存在的源路径");
        string single = await client.CompressAsync("中文文件夹/数据.bin");
        using (var archive = ZipFile.OpenRead(Path.Combine(root, single)))
        {
            using var input = archive.GetEntry("数据.bin")!.Open(); using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            check(archive.Entries.Count == 1 && memory.ToArray().SequenceEqual(content), "任意类型单文件可生成 ZIP 且内容一致");
        }
        await RejectAsync(() => client.DownloadZipAsync("中文文件夹/数据.bin", Path.Combine(testRoot, "不能下载.bin")), check, "ZIP 下载端点拒绝其他文件类型");
        string lockedFolder = Path.Combine(root, "占用测试");
        Directory.CreateDirectory(lockedFolder);
        await File.WriteAllTextAsync(Path.Combine(lockedFolder, "正常.txt"), "正常");
        string lockedFile = Path.Combine(lockedFolder, "正在写入.log");
        await File.WriteAllTextAsync(lockedFile, "日志内容");
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            string lockedZip = await client.CompressAsync("占用测试");
            using var archive = ZipFile.OpenRead(Path.Combine(root, lockedZip));
            check(archive.GetEntry("占用测试/正常.txt") != null && archive.GetEntry("占用测试/正在写入.log") == null, "压缩跳过被独占的文件并保留其余内容");
            await RejectAsync(() => client.CompressAsync("占用测试/正在写入.log"), check, "直接压缩被独占文件时提示文件正在使用");
        }
        using (var cancel = new CancellationTokenSource())
        {
            string partial = Path.Combine(testRoot, "取消.zip");
            try
            {
                await client.DownloadZipAsync(zip, partial, new Callback<RemoteTransferProgress>(p => { if (p.Stage == "下载") cancel.Cancel(); }), cancel.Token);
                throw new Exception("取消下载没有生效");
            }
            catch (OperationCanceledException) { }
            check(!File.Exists(partial) && !Directory.GetFiles(testRoot, "取消.zip.*.part").Any(), "取消 ZIP 下载清理临时文件且不提交目标");
        }
        // 模拟客户端在压缩第一条进度后断线，等待服务端清理并释放写锁。
        using (var socket = await Wire.ConnectAsync("127.0.0.1", server.Port, default))
        {
            await Wire.WriteAsync(socket.GetStream(), new RemoteRequest("compress", "中文文件夹"));
            await Wire.ReadAsync<RemoteResponse>(socket.GetStream());
            socket.Client.LingerState = new LingerOption(true, 0);
        }
        await Task.Delay(100);
        string next = await client.CompressAsync("中文文件夹");
        check(File.Exists(Path.Combine(root, next)) && !Directory.GetFiles(root, ".remote-archive-*", SearchOption.AllDirectories).Any(), "压缩连接中断后清理临时文件且后续压缩可运行");
        string rootZip = await client.CompressAsync("");
        using (var archive = ZipFile.OpenRead(Path.Combine(root, rootZip)))
            check(archive.Entries.All(e => e.FullName != "zip-root/" + rootZip && !e.FullName.Contains(".remote-archive-")), "压缩共享根目录不会递归包含当前 ZIP 或临时文件");
        Directory.CreateDirectory(Path.Combine(root, "仅空目录"));
        string emptyZip = await client.CompressAsync("仅空目录");
        using (var archive = ZipFile.OpenRead(Path.Combine(root, emptyZip)))
            check(archive.Entries.Count == 1 && archive.Entries[0].FullName == "仅空目录/", "纯空文件夹可创建有效 ZIP");
        // 伪造错误的服务端哈希，确认客户端不会把损坏的文件提交为正式 ZIP。
        var fake = new TcpListener(System.Net.IPAddress.Loopback, 0); fake.Start();
        try
        {
            var send = Task.Run(async () =>
            {
                using var socket = await fake.AcceptTcpClientAsync(); var stream = socket.GetStream();
                await Wire.ReadAsync<RemoteRequest>(stream);
                await Wire.WriteAsync(stream, new RemoteResponse(true, Version: new string('0', 64), Length: 3, Stage: "接收数据"));
                await stream.WriteAsync(new byte[] { 1, 2, 3 });
            });
            string bad = Path.Combine(testRoot, "坏文件.zip");
            await RejectAsync(() => new RemoteFileClient("127.0.0.1", ((System.Net.IPEndPoint)fake.LocalEndpoint).Port).DownloadZipAsync("bad.zip", bad), check, "ZIP 哈希错误拒绝提交");
            await send;
            check(!File.Exists(bad) && !Directory.GetFiles(testRoot, "坏文件.zip.*.part").Any(), "ZIP 校验失败清理二进制临时文件");
        }
        finally { fake.Stop(); }
        var zipEntry = (await client.CallAsync(new("list"))).Entries!.Single(e => e.Path == zip);
        await client.DeleteFileAsync(zipEntry);
        check(!File.Exists(Path.Combine(root, zip)) && Directory.Exists(Path.Combine(root, "中文文件夹")) && File.Exists(Path.Combine(root, "中文文件夹", "数据.bin")), "删除多余 ZIP 保留原文件夹和源文件");
        check(!(await client.CallAsync(new("list"))).Entries!.Any(e => e.Path == zip), "删除 ZIP 后刷新列表移除对应记录");
        var raw = (await client.CallAsync(new("list", "中文文件夹"))).Entries!.Single(e => e.Name == "空文件");
        await File.WriteAllTextAsync(Path.Combine(root, raw.Path), "已改变");
        await RejectAsync(() => client.DeleteFileAsync(raw), check, "文件在列表加载后变化时拒绝删除");
        await RejectAsync(() => client.CallAsync(new("delete", "../outside.zip")), check, "删除拒绝共享目录越界");
        await RejectAsync(() => client.CallAsync(new("delete", "中文文件夹")), check, "删除拒绝文件夹和递归删除");
        await RejectAsync(() => client.CallAsync(new("delete", "")), check, "删除拒绝共享根目录");
        await RejectAsync(() => client.CallAsync(new("delete", "中文文件夹.zip")), check, "删除缺少文件快照时拒绝执行");
    }
    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string message)
    { try { await action(); } catch (IOException) { check(true, message); return; } throw new Exception("未拒绝：" + message); }
    private static async Task WaitAsync(Func<bool> done)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!done()) await Task.Delay(20, timeout.Token); }
}
