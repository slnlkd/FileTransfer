using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Transfer.Core;
using Transfer.Core.Remote;

internal static class RemoteChecks
{
    public static async Task RunAsync(string testRoot, Action<bool, string> check)
    {
        string root = Path.Combine(testRoot, "remote-root"); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "子目录"));
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "第一行\r\n第二行", new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.Combine(root, "data.json"), "{\"状态\":\"正常\"}");
        await File.WriteAllTextAsync(Path.Combine(root, "trace.log"), "中文 GBK 日志", Encoding.GetEncoding(936));
        await File.WriteAllTextAsync(Path.Combine(root, "other.bin"), "不预览其他类型");
        byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a9sAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(Path.Combine(root, "late.png"), image);
        await File.WriteAllBytesAsync(Path.Combine(root, "early.png"), image);
        File.SetCreationTimeUtc(Path.Combine(root, "late.png"), DateTime.UtcNow);
        File.SetCreationTimeUtc(Path.Combine(root, "early.png"), DateTime.UtcNow.AddDays(-1));
        await using var server = new RemoteFileService(root); server.Start(0);
        var client = new RemoteFileClient("127.0.0.1", server.Port);
        var list = (await client.CallAsync(new("list"))).Entries!;
        check(list.Any(e => e.IsDirectory && e.Name == "子目录") && list.Any(e => e.Name == "hello.txt"), "远程文件管理返回文件和目录");
        var text = await client.CallAsync(new("text", "hello.txt"));
        check(text.Text == "第一行\r\n第二行" && text.EncodingName == "65001:bom", "读取中文 UTF8 BOM 文本");
        var saved = await client.CallAsync(new("save", "hello.txt", "修改后\r\n", text.Version, text.EncodingName));
        check((await File.ReadAllTextAsync(Path.Combine(root, "hello.txt"))) == "修改后\r\n" && saved.Version != text.Version, "远程修改文本并提交新版本");
        check((await File.ReadAllBytesAsync(Path.Combine(root, "hello.txt"))).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "保存保留 UTF8 BOM 编码");
        await ExpectRejected(() => client.CallAsync(new("save", "hello.txt", "旧版本覆盖", text.Version, text.EncodingName)), check, "拒绝使用过期文本版本覆盖");
        check((await client.CallAsync(new("text", "data.json"))).Text!.Contains("正常"), "JSON 可以查看内容");
        var gbk = await client.CallAsync(new("text", "trace.log"));
        check(gbk.Text == "中文 GBK 日志" && gbk.EncodingName == "936:plain", "读取 GBK log 文本");
        await client.CallAsync(new("save", "trace.log", "修改 GBK 日志", gbk.Version, gbk.EncodingName));
        check(Encoding.GetEncoding(936).GetString(await File.ReadAllBytesAsync(Path.Combine(root, "trace.log"))) == "修改 GBK 日志", "保存保留 GBK 编码");
        await ExpectRejected(() => client.CallAsync(new("text", "other.bin")), check, "其他文件类型不提供文本预览");
        foreach (string path in new[] { "../outside.txt", "/absolute", "hello.txt:stream", "子目录/../../outside", "C:\\Windows" })
            await ExpectRejected(() => client.CallAsync(new("text", path)), check, "拒绝超出共享根目录或数据流路径 " + path);
        check((await client.ReadImageAsync("early.png")).SequenceEqual(image), "TCP 二进制图片传输一致");
        check(list.Where(e => e.IsImage).OrderBy(e => e.CreatedUtc).First().Name == "early.png", "图片排序使用创建时间");
        await client.CallAsync(new("mkdir", "子目录/新文件夹"));
        check(Directory.Exists(Path.Combine(root, "子目录", "新文件夹")), "远程新建文件夹");
        string local = Path.Combine(testRoot, "upload.json"); await File.WriteAllTextAsync(local, "上传测试");
        await client.UploadAsync(local, "子目录/upload.json", false);
        check(await File.ReadAllTextAsync(Path.Combine(root, "子目录", "upload.json")) == "上传测试", "客户端上传文件并校验提交");
        await ExpectRejected(() => client.UploadAsync(local, "子目录/upload.json", false), check, "上传同名文件需显式覆盖");
        await File.WriteAllTextAsync(local, "覆盖后");
        await client.UploadAsync(local, "子目录/upload.json", true);
        check(await File.ReadAllTextAsync(Path.Combine(root, "子目录", "upload.json")) == "覆盖后", "上传覆盖提交成功");
        using (var socket = await Wire.ConnectAsync("127.0.0.1", server.Port, default))
        {
            var stream = socket.GetStream();
            await Wire.WriteAsync(stream, new RemoteRequest("upload", "子目录/upload.json", Length: 100, Sha256: new string('0', 64), Overwrite: true));
            check((await Wire.ReadAsync<RemoteResponse>(stream)).Ok, "中断上传测试获得接收许可");
            await stream.WriteAsync(new byte[] { 1, 2 });
        }
        await WaitUntilAsync(() => !Directory.EnumerateFiles(root, ".remote-upload-*", SearchOption.AllDirectories).Any());
        check(await File.ReadAllTextAsync(Path.Combine(root, "子目录", "upload.json")) == "覆盖后", "上传中断保留原文件且清理临时文件");
        using (var socket = await Wire.ConnectAsync("127.0.0.1", server.Port, default))
        {
            var stream = socket.GetStream();
            await Wire.WriteAsync(stream, new RemoteRequest("upload", "bad.json", Length: 2, Sha256: new string('0', 64)));
            await Wire.ReadAsync<RemoteResponse>(stream); await stream.WriteAsync(new byte[] { 1, 2 });
            check(!(await Wire.ReadAsync<RemoteResponse>(stream)).Ok && !File.Exists(Path.Combine(root, "bad.json")), "上传哈希错误不提交目标文件");
        }
        int port = server.Port; await server.StopAsync(); server.Start(port);
        check((await client.CallAsync(new("list"))).Ok, "远程文件管理停止后可同端口重启");
        await FolderChecksAsync(testRoot, check);
        await ProcessChecksAsync(root, client, testRoot, check);
        await ArchiveChecks.RunAsync(testRoot, check);
    }
    private static async Task ExpectRejected(Func<Task> operation, Action<bool, string> check, string description)
    {
        try { await operation(); }
        catch (IOException) { check(true, description); return; }
        throw new Exception("未拒绝：" + description);
    }
    private static async Task WaitUntilAsync(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!done()) await Task.Delay(30, timeout.Token);
    }
    private static async Task FolderChecksAsync(string testRoot, Action<bool, string> check)
    {
        string folder = Path.Combine(testRoot, "shared-folder");
        Directory.CreateDirectory(Path.Combine(folder, "a")); Directory.CreateDirectory(Path.Combine(folder, "b"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        await File.WriteAllTextAsync(Path.Combine(folder, "a", "same.txt"), "相同内容");
        await File.WriteAllTextAsync(Path.Combine(folder, "b", "same.txt"), "相同内容");
        await using var server = new FileServer(); await server.AddFolderAsync(folder); server.Start(0);
        var list = await Downloader.ListAsync("127.0.0.1", server.Port);
        check(list.Count(e => !e.IsDirectory) == 2 && list.Where(e => !e.IsDirectory).Select(e => e.Id).Distinct().Count() == 2, "共享目录内相同名字和内容的文件拥有独立地址");
        check(list.Any(e => e.IsDirectory && e.RelativePath == Path.Combine("shared-folder", "empty")), "共享目录保留空子文件夹记录");
        string output = Path.Combine(testRoot, "folder-download");
        foreach (var file in list.Where(f => !f.IsDirectory)) await new Downloader().DownloadAsync("127.0.0.1", server.Port, file, output, 2);
        check(File.Exists(Path.Combine(output, "shared-folder", "a", "same.txt")) && File.Exists(Path.Combine(output, "shared-folder", "b", "same.txt")), "下载共享文件夹按相对路径分别保存");
        await File.WriteAllTextAsync(Path.Combine(folder, "new.txt"), "新增文件");
        list = await Downloader.ListAsync("127.0.0.1", server.Port);
        check(list.Any(e => e.Name == "new.txt"), "客户端刷新发现共享文件夹新增文件");
        var removed = list.Single(e => e.Name == "new.txt"); server.RemoveFile(removed.Id);
        check(!(await Downloader.ListAsync("127.0.0.1", server.Port)).Any(e => e.Id == removed.Id), "移除目录内文件后刷新不会重新共享");
        await using var restored = new FileServer(); restored.RestoreExclusions(server.ExcludedPaths); await restored.AddFolderAsync(folder);
        check(!restored.Files.Any(e => e.Name == "new.txt"), "重建服务时恢复目录内移除记录");
        var root = server.Files.Single(e => e.IsDirectory && e.Name == "shared-folder"); server.RemoveFile(root.Id);
        check((await Downloader.ListAsync("127.0.0.1", server.Port)).Length == 0 && Directory.Exists(folder), "移除共享目录及子项但保留源目录");
    }
    private static async Task ProcessChecksAsync(string root, RemoteFileClient client, string testRoot, Action<bool, string> check)
    {
        string fixture = Environment.GetEnvironmentVariable("FILETRANSFER_TEST_FIXTURE") ?? AppContext.BaseDirectory;
        string application = Path.Combine(root, "app"); Directory.CreateDirectory(application);
        foreach (var file in Directory.GetFiles(fixture)) File.Copy(file, Path.Combine(application, Path.GetFileName(file)), true);
        // 每次测试使用独立进程名，避免其他测试实例或权限不同的同名进程干扰状态检查。
        string fixtureName = "RemoteFixture_" + Guid.NewGuid().ToString("N") + ".exe";
        File.Copy(Path.Combine(application, "Transfer.SmokeTests.exe"), Path.Combine(application, fixtureName));
        string exe = "app/" + fixtureName;
        Environment.SetEnvironmentVariable("FILETRANSFER_PROCESS_FIXTURE", "1");
        try
        {
            check((await client.CallAsync(new("status", exe))).Process!.ProcessIds.Length == 0, "EXE 初始状态未运行");
            await client.CallAsync(new("start", exe));
            var running = (await client.CallAsync(new("status", exe))).Process!;
            check(running.ProcessIds.Length == 1 && running.State == "运行中", "启动共享目录 EXE 并按完整路径获取 PID");
            await ExpectRejected(() => client.UploadAsync(Path.Combine(application, fixtureName), exe, true), check, "拒绝覆盖运行中的 EXE");
            await ExpectRejected(() => client.CallAsync(new("start", exe)), check, "拒绝重复启动同一 EXE");
            await client.CallAsync(new("stop", exe));
            check((await client.CallAsync(new("status", exe))).Process!.ProcessIds.Length == 0, "停止 EXE 并确认退出");
            await client.UploadAsync(Path.Combine(fixture, "Transfer.SmokeTests.exe"), exe, true);
            await client.CallAsync(new("start", exe));
            check((await client.CallAsync(new("status", exe))).Process!.ProcessIds.Length == 1, "停止上传更新后可重新运行 EXE");
        }
        finally
        {
            await client.CallAsync(new("stop", exe));
            Environment.SetEnvironmentVariable("FILETRANSFER_PROCESS_FIXTURE", null);
        }
    }
}
