using Serilog;
using System.Security.Cryptography;
using System.Text.Json;
using Transfer.Core;

if (Environment.GetEnvironmentVariable("FILETRANSFER_PROCESS_FIXTURE") == "1")
{
    await Task.Delay(Timeout.Infinite); return;
}
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
if (args.Length == 4 && args[0] == "crash")
{
    var crashFile = (await Downloader.ListAsync(args[1], int.Parse(args[2]))).First(f => f.Length > 0);
    await new Downloader().DownloadAsync(args[1], int.Parse(args[2]), crashFile, args[3], 4,
        new InlineProgress(p => { if (p.Completed >= 6 * 1048576) Environment.Exit(23); }));
    return;
}

string root = Path.Combine(Path.GetTempPath(), "FileTransfer-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string loggingConfig = Path.Combine(root, "logging.json");
string logs = Path.Combine(root, "logs");
await File.WriteAllTextAsync(loggingConfig, JsonSerializer.Serialize(new LoggingOptions { Directory = logs }));
AppLogging.Initialize("SmokeTests", loggingConfig);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("失败：" + name);
    Console.WriteLine("通过：" + name); passed++;
}
async Task Verify(string path, SharedFile file)
{
    await using var input = File.OpenRead(path);
    Check(Convert.ToHexString(await SHA256.HashDataAsync(input)) == file.Sha256, "完整文件 SHA-256 一致");
}
try
{
    string source = Path.Combine(root, "中文测试文件.bin");
    await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(24 * 1024 * 1024 + 137));
    await using var server = new FileServer();
    var shared = await server.AddFileAsync(source);
    server.Start(0);
    string host = "127.0.0.1"; int port = server.Port;
    var list = await Downloader.ListAsync(host, port);
    Check(list.Length == 1 && list[0].Id == shared.Id, "TCP 获取中文文件列表");
    await Task.WhenAll(server.StopAsync(), server.StopAsync());
    Check(!server.IsRunning, "停止服务及重复停止安全完成");
    bool stoppedRejected = false;
    try { await Downloader.ListAsync(host, port); }
    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { stoppedRejected = true; }
    Check(stoppedRejected, "服务停止后不再接受连接");
    server.Start(port);
    Check(server.IsRunning && (await Downloader.ListAsync(host, port)).Any(f => f.Id == shared.Id), "同一服务对象重新启动并保留共享列表");
    bool serverComplete = false;
    server.Progress += p => { if (p.State == "已完成") serverComplete = true; };
    string folder = Path.Combine(root, "resume");
    using var cancel = new CancellationTokenSource();
    try
    {
        await new Downloader().DownloadAsync(host, port, shared, folder, 4,
            new InlineProgress(p => { if (p.Completed >= 1048576) cancel.Cancel(); }), cancel.Token);
        throw new Exception("暂停未生效");
    }
    catch (OperationCanceledException) { }
    string part = Directory.GetFiles(folder, "*.part").Single();
    string checkpoint = await File.ReadAllTextAsync(part + ".json");
    using var json = JsonDocument.Parse(checkpoint);
    long saved = json.RootElement.GetProperty("Offsets").EnumerateArray().Sum(x => x.GetInt64());
    Check(saved > 0 && saved < shared.Length && !File.Exists(Path.Combine(folder, shared.Name)), "暂停保留二进制数据和非零分块断点");
    long firstProgress = -1;
    await new Downloader().DownloadAsync(host, port, shared, folder, 8,
        new InlineProgress(p => Interlocked.CompareExchange(ref firstProgress, p.Completed, -1)));
    Check(firstProgress >= saved, "新下载实例读取原断点并更改并发数继续");
    await Verify(Path.Combine(folder, shared.Name), shared);
    Check(Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(new[] { shared.Name }) && serverComplete, "成功续传后仅保留正式文件，清理 part、json、tmp 和 lock");

    string empty = Path.Combine(root, "空文件.txt");
    await File.WriteAllBytesAsync(empty, []);
    var emptyInfo = await server.AddFileAsync(empty);
    await new Downloader().DownloadAsync(host, port, emptyInfo, Path.Combine(root, "empty"), 4);
    Check(new FileInfo(Path.Combine(root, "empty", emptyInfo.Name)).Length == 0, "空文件下载");
    Check(Directory.GetFiles(Path.Combine(root, "empty")).Length == 1, "空文件成功后也不残留辅助文件");
    Check(server.RemoveFile(emptyInfo.Id), "移除服务端共享记录");
    Check(!(await Downloader.ListAsync(host, port)).Any(f => f.Id == emptyInfo.Id) && File.Exists(empty), "客户端刷新不再显示已移除文件且源文件保留");
    try { await Wire.CallAsync(host, port, new("range", emptyInfo.Id, 0, 1), default); throw new Exception("移除后仍可请求"); }
    catch (IOException) { Check(true, "移除共享后拒绝新的分块请求"); }
    Check((await server.AddFileAsync(empty)).Id == emptyInfo.Id, "已移除的源文件可再次添加共享");
    var invalidRangeRequest = new Request("range", shared.Id, -1, 100);
    try { await Wire.CallAsync(host, port, invalidRangeRequest, default); throw new Exception("范围未拒绝"); }
    catch (IOException) { Check(true, "拒绝非法分块范围"); }
    try { await Wire.CallAsync(host, port, new("range", "missing", 0, 100), default); throw new Exception("文件未拒绝"); }
    catch (IOException) { Check(true, "拒绝未共享文件"); }

    string conflict = Path.Combine(root, "conflict"); Directory.CreateDirectory(conflict);
    await File.WriteAllTextAsync(Path.Combine(conflict, shared.Name), "不能覆盖");
    try { await new Downloader().DownloadAsync(host, port, shared, conflict, 4); throw new Exception("覆盖保护失败"); }
    catch (IOException) { Check(await File.ReadAllTextAsync(Path.Combine(conflict, shared.Name)) == "不能覆盖", "同名文件保护且保留完整临时文件"); }
    File.Delete(Path.Combine(conflict, shared.Name));
    var corruptPart = Directory.GetFiles(conflict, "*.part").Single();
    await using (var data = new FileStream(corruptPart, FileMode.Open, FileAccess.Write)) { data.WriteByte(0); data.WriteByte(0); data.WriteByte(0); }
    try { await new Downloader().DownloadAsync(host, port, shared, conflict, 4); throw new Exception("损坏未发现"); }
    catch (IOException ex) when (ex.Message.Contains("SHA-256")) { Check(true, "发现临时文件损坏并重置断点"); }
    await new Downloader().DownloadAsync(host, port, shared, conflict, 4);
    await Verify(Path.Combine(conflict, shared.Name), shared);

    var interruptedServer = new FileServer();
    var interruptedInfo = await interruptedServer.AddFileAsync(source);
    interruptedServer.Start(0); int interruptedPort = interruptedServer.Port;
    Task? shutdown = null;
    string brokenFolder = Path.Combine(root, "broken");
    try
    {
        await new Downloader().DownloadAsync(host, interruptedPort, interruptedInfo, brokenFolder, 4,
            new InlineProgress(p => { if (p.Completed > 1048576 && shutdown == null) shutdown = interruptedServer.StopAsync(); }));
        throw new Exception("断开未触发");
    }
    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { }
    if (shutdown != null) await shutdown;
    Check(Directory.GetFiles(brokenFolder, "*.part.json").Length == 1, "服务端关闭后保留断点");
    interruptedServer.Start(interruptedPort);
    await new Downloader().DownloadAsync(host, interruptedPort, interruptedInfo, brokenFolder, 4);
    await Verify(Path.Combine(brokenFolder, shared.Name), shared);
    await interruptedServer.DisposeAsync();
    string crashFolder = Path.Combine(root, "crash");
    var processInfo = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    processInfo.ArgumentList.Add(typeof(InlineProgress).Assembly.Location);
    processInfo.ArgumentList.Add("crash"); processInfo.ArgumentList.Add(host);
    processInfo.ArgumentList.Add(port.ToString()); processInfo.ArgumentList.Add(crashFolder);
    using (var child = System.Diagnostics.Process.Start(processInfo)!)
    {
        await child.WaitForExitAsync();
        Check(child.ExitCode == 23, "下载进程未执行清理即强制退出");
    }
    var crashState = Directory.GetFiles(crashFolder, "*.part.json").Single();
    using (var savedCrash = JsonDocument.Parse(await File.ReadAllTextAsync(crashState)))
        Check(savedCrash.RootElement.GetProperty("Offsets").EnumerateArray().Sum(x => x.GetInt64()) > 0, "强制退出后磁盘中仍保留有效断点");
    await new Downloader().DownloadAsync(host, port, shared, crashFolder, 4);
    await Verify(Path.Combine(crashFolder, shared.Name), shared);
    await File.AppendAllTextAsync(source, "changed");
    try { await Wire.CallAsync(host, port, new("range", shared.Id, 0, 100), default); throw new Exception("源文件修改未发现"); }
    catch (IOException ex) when (ex.Message.Contains("源文件已修改")) { Check(true, "源文件变更后拒绝旧版本下载"); }
    string blockedFolder = Path.Combine(root, "blocked-checkpoint");
    Directory.CreateDirectory(blockedFolder);
    Directory.CreateDirectory(Path.Combine(blockedFolder, shared.Name + "." + shared.Sha256[..16] + ".part.json.tmp"));
    try { await new Downloader().DownloadAsync(host, port, shared, blockedFolder, 4); throw new Exception("断点写入失败未触发"); }
    catch (UnauthorizedAccessException) { Check(true, "断点写入失败保留原始异常类型"); }
    try { throw new InvalidOperationException("日志异常堆栈探针", new IOException("内部异常探针")); }
    catch (Exception probe) { Log.Error(probe, "日志异常写入验证 {ProbeId}", "logging-probe"); }
    Log.CloseAndFlush();
    // 关闭日志文件句柄后验证真实传输日志和结构化关联字段。
    string appLog = string.Join("\n", Directory.GetFiles(logs, "app-*.log").Select(File.ReadAllText));
    string errorLog = string.Join("\n", Directory.GetFiles(logs, "error-*.log").Select(File.ReadAllText));
    Check(appLog.Contains("断点已持久化") && appLog.Contains("SHA-256 校验通过") && appLog.Contains("本地下载状态"), "真实传输记录断点、校验及状态日志");
    Check(appLog.Contains("\"Session\"") && appLog.Contains("\"FileId\"") && appLog.Contains("\"RequestId\"") && appLog.Contains("\"ChunkIndex\"") && appLog.Contains("\"ThreadId\""), "日志包含会话、文件、请求、分块和线程关联字段");
    Check(errorLog.Contains("System.InvalidOperationException: 日志异常堆栈探针") && errorLog.Contains("SHA-256 校验不一致") && errorLog.Contains("处理 TCP 请求失败"), "独立错误日志保留异常类型和传输故障");
    Check(errorLog.Contains("内部异常探针") && errorLog.Contains("Program.cs:line"), "错误日志包含内部异常和源码堆栈行号");
    Check(errorLog.Contains("异常退出时保存断点失败") && errorLog.Contains("原异常") && errorLog.Contains("UnauthorizedAccessException"), "断点保存故障与原始故障均有日志");
    Check(System.Text.RegularExpressions.Regex.Matches(appLog, invalidRangeRequest.RequestId).Count >= 2, "同一 RequestId 可关联客户端请求和服务端故障");
    Check(!errorLog.Contains("断点已持久化"), "错误日志过滤普通 Debug 事件");
    Log.CloseAndFlush();
    await RemoteChecks.RunAsync(root, Check);
    string filteredLogs = Path.Combine(root, "filtered-logs");
    await File.WriteAllTextAsync(loggingConfig, JsonSerializer.Serialize(new LoggingOptions { Directory = filteredLogs, MinimumLevel = "Warning" }));
    AppLogging.Initialize("SmokeTests", loggingConfig);
    Log.Information("不应出现的普通日志"); Log.Warning("应出现的警告日志"); Log.CloseAndFlush();
    string filteredText = string.Join("\n", Directory.GetFiles(filteredLogs, "app-*.log").Select(File.ReadAllText));
    Check(!filteredText.Contains("不应出现") && filteredText.Contains("应出现"), "配置文件日志级别过滤生效");
    string rolledLogs = Path.Combine(root, "rolled-logs");
    await File.WriteAllTextAsync(loggingConfig, JsonSerializer.Serialize(new LoggingOptions { Directory = rolledLogs, FileSizeLimitBytes = 1024, RetainedFileCountLimit = 3 }));
    AppLogging.Initialize("SmokeTests", loggingConfig);
    for (int i = 0; i < 20; i++) Log.Information("滚动日志验证 {Index} {Padding}", i, new string('x', 700));
    Log.CloseAndFlush();
    Check(Directory.GetFiles(rolledLogs, "app-*.log").Length == 3, "日志按大小滚动且限制保留数量");
    string fallbackLogs = Path.Combine(root, "fallback-logs");
    Environment.SetEnvironmentVariable("FILETRANSFER_LOG_DIRECTORY", fallbackLogs);
    await File.WriteAllTextAsync(loggingConfig, "invalid-json");
    AppLogging.Initialize("SmokeTests", loggingConfig); Log.CloseAndFlush();
    Check(string.Join("\n", Directory.GetFiles(fallbackLogs, "app-*.log").Select(File.ReadAllText)).Contains("读取日志配置失败"), "配置损坏时回退默认配置并保留异常日志");
    Environment.SetEnvironmentVariable("FILETRANSFER_LOG_DIRECTORY", null);
    Console.WriteLine($"全部通过，共 {passed} 项检查。");
}
finally
{
    Log.CloseAndFlush();
    // 只清理本测试创建的唯一临时目录。
    Directory.Delete(root, true);
}
sealed class InlineProgress(Action<TransferProgress> callback) : IProgress<TransferProgress>
{
    public void Report(TransferProgress value) => callback(value);
}
