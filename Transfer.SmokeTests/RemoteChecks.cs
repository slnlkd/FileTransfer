using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        check((await client.CallAsync(new("hello"))).Protocol == RemoteProtocol.Version, "客户端与服务端协议版本握手一致");
        string updateRoot = Path.Combine(testRoot, "updates");
        Directory.CreateDirectory(updateRoot);
        byte[] updateBytes = Encoding.UTF8.GetBytes("update-package");
        await File.WriteAllBytesAsync(Path.Combine(updateRoot, "client.zip"), updateBytes);
        string updateHash = Convert.ToHexString(SHA256.HashData(updateBytes));
        await File.WriteAllTextAsync(Path.Combine(updateRoot, "manifest.json"),
            JsonSerializer.Serialize(new { version = "9.9.9", file = "client.zip", sha256 = updateHash, notes = "测试更新" }));
        server.UpdateDirectory = updateRoot;
        var updateInfo = await client.CheckUpdateAsync();
        check(updateInfo.Version == "9.9.9" && updateInfo.Sha256 == updateHash && updateInfo.Length == updateBytes.Length, "读取客户端更新清单");
        string updateDestination = Path.Combine(testRoot, "update-download.zip");
        await client.DownloadUpdateAsync(updateInfo, updateDestination);
        check((await File.ReadAllBytesAsync(updateDestination)).SequenceEqual(updateBytes), "更新包下载与 SHA-256 校验");
        string pageRoot = Path.Combine(root, "分页");
        Directory.CreateDirectory(pageRoot);
        for (int i = 1; i <= 620; i++) await File.WriteAllTextAsync(Path.Combine(pageRoot, i + ".txt"), "");
        var all = await client.CallAsync(new("list", "分页"));
        check(all.Total == 620 && all.Entries!.Length == 620, "列表返回总数和全部条目");
        check(all.Entries![0].Name == "1.txt" && all.Entries[1].Name == "2.txt" && all.Entries[9].Name == "10.txt", "列表按名称自然排序");
        var firstPage = await client.CallAsync(new("list", "分页", Take: 200));
        var secondPage = await client.CallAsync(new("list", "分页", Skip: 200, Take: 200));
        check(firstPage.Entries!.Length == 200 && firstPage.Total == 620, "列表分页返回指定数量并报告总数");
        check(secondPage.Entries![0].Name == "201.txt" && secondPage.Total == 620, "列表分页跳过偏移正确");
        var foldersOnly = await client.CallAsync(new("list", "分页", FoldersOnly: true));
        check(foldersOnly.Entries!.All(e => e.IsDirectory) && foldersOnly.Total == 0, "目录树请求只返回文件夹");
        int expectedFiltered = Enumerable.Range(1, 620).Count(i => i.ToString().StartsWith('1'));
        await using var filteredServer = new RemoteFileService(pageRoot)
        {
            ListFilter = (_, _, items) => Task.FromResult(new RemoteFilterResult(items.Where(e => e.Name.StartsWith('1')).ToArray()))
        };
        filteredServer.Start(0);
        var filteredPage = await new RemoteFileClient("127.0.0.1", filteredServer.Port)
            .CallAsync(new("list", "", Filter: "测试", Take: 5));
        check(filteredPage.Total == expectedFiltered && filteredPage.Entries!.Length == 5, "过滤后分页总数正确");
        var noFilterServer = await client.CallAsync(new("list", "", Filter: "测试"));
        check(noFilterServer.Warning is not null && noFilterServer.Warning.Contains("未启用目录过滤")
            && noFilterServer.Entries!.Length == noFilterServer.Total, "服务端未启用过滤时提示并显示全部");
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        int attempts = 0;
        var serve = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (socket)
                    {
                        var stream = socket.GetStream();
                        await Wire.ReadAsync<RemoteRequest>(stream);
                        // 第一次连接直接断开，模拟网络抖动；第二次正常返回。
                        if (Interlocked.Increment(ref attempts) == 1) return;
                        await Wire.WriteAsync(stream, new RemoteResponse(true, Entries: [], Total: 0));
                    }
                });
            }
        });
        var retryClient = new RemoteFileClient("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        var retried = await retryClient.CallAsync(new("list"));
        check(attempts == 2 && retried.Ok, "列表连接中断后自动重试一次");
        listener.Stop(); await serve;
        var text = await client.CallAsync(new("text", "hello.txt"));
        check(text.Text == "第一行\r\n第二行" && text.EncodingName == "65001:bom", "读取中文 UTF8 BOM 文本");
        var saved = await client.CallAsync(new("save", "hello.txt", "修改后\r\n", text.Version, text.EncodingName));
        check((await File.ReadAllTextAsync(Path.Combine(root, "hello.txt"))) == "修改后\r\n" && saved.Version != text.Version, "远程修改文本并提交新版本");
        check((await File.ReadAllBytesAsync(Path.Combine(root, "hello.txt"))).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "保存保留 UTF8 BOM 编码");
        string singleDownload = Path.Combine(testRoot, "单文件下载.txt");
        await client.DownloadFileAsync("hello.txt", singleDownload);
        check(await File.ReadAllTextAsync(singleDownload) == "修改后\r\n", "单文件流式下载内容一致");
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
        await AnnotationChecksAsync(testRoot, image, check);
        await FolderChecksAsync(testRoot, check);
        await ProcessChecksAsync(root, client, testRoot, check);
        await ArchiveChecks.RunAsync(testRoot, check);
    }
    // 标注粒度是「一次对线」：机台/yyyyMMdd/批次，一个文件夹一条记录，重复提交覆盖。
    private static async Task AnnotationChecksAsync(string testRoot, byte[] image, Action<bool, string> check)
    {
        string root = Path.Combine(testRoot, "annotation-root");
        string folder = Path.Combine(root, "A6-DPQG-012", "20261118", "9");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(root, "A6-DPQG-012", "20261118", "10"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "a.png"), image);
        await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "非图片");
        string repositoryPath = Path.Combine(testRoot, "annotations.json");
        await File.WriteAllTextAsync(repositoryPath, JsonSerializer.Serialize(new
        {
            dictionary = new
            {
                categories = new[] { new { code = "外观异常", name = "外观异常" }, new { code = "尺寸超差", name = "尺寸超差" } },
                plans = new[] { new { code = "重新拍摄", name = "重新拍摄" }, new { code = "人工复检", name = "人工复检" } }
            },
            annotations = Array.Empty<object>()
        }));
        var repository = new JsonAnnotationRepository(repositoryPath);
        await using var server = new RemoteFileService(root, repository); server.Start(0);
        var client = new RemoteFileClient("127.0.0.1", server.Port);

        var dictionary = await client.GetAnnotationDictionaryAsync();
        check(dictionary.Categories.Length == 2 && dictionary.Plans.Length == 2, "读取标注字典");

        var saved = await client.SaveAnnotationAsync("A6-DPQG-012/20261118/9", "外观异常", "重新拍摄", "第一版", "李明", "a.png");
        check(saved.CategoryName == "外观异常" && saved.PlanName == "重新拍摄" && saved.Operator == "李明"
            && saved.EquipmentNo == "A6-DPQG-012" && saved.BatchNumber == "9" && saved.AutoLineDate?.Day == 18
            && saved.ImageName == "a.png",
            "按对线文件夹保存标注并记录机台/日期/批次/图片名");

        var second = await client.SaveAnnotationAsync("A6-DPQG-012/20261118/9", "尺寸超差", "人工复检", "第二版", "王芳", "a.png");
        check(second.CategoryName == "尺寸超差" && second.Note == "第二版", "同一张图可新增第二条标注");
        await client.SaveAnnotationAsync("A6-DPQG-012/20261118/9", "外观异常", "人工复检", "另一张", "李明", "b.png");

        var fetched = await client.GetAnnotationAsync("A6-DPQG-012/20261118/9");
        check(fetched is { ImageName: "b.png", Note: "另一张" }, "旧版单条查询仍返回批次最新标注");

        var listed = await client.ListAnnotationsAsync("A6-DPQG-012/20261118");
        check(listed.Length == 3 && listed.All(item => item.BatchNumber == "9")
            && listed.Where(item => item.ImageName == "a.png").Select(item => item.Note).SequenceEqual(new[] { "第二版", "第一版" }),
            "重复提交保留同图历史，不同图片各自新增");
        var history = await client.GetAnnotationHistoryAsync("A6-DPQG-012/20261118/9");
        check(history.SequenceEqual(listed), "读取批次内所有图片的标注历史");

        await ExpectRejected(() => client.SaveAnnotationAsync("A6-DPQG-012/20261118/10", "外观异常", "重新拍摄", null, "李明", "../x.png"), check, "图片名包含路径分隔符拒绝标注");
        await ExpectRejected(() => client.SaveAnnotationAsync("A6-DPQG-012/20261118/10", "外观异常", "重新拍摄", null, " "), check, "操作人为空拒绝标注");
        await ExpectRejected(() => client.SaveAnnotationAsync("A6-DPQG-012/20261118/10", "未知归类", "重新拍摄", null, "李明"), check, "字典外的归类拒绝标注");
        await ExpectRejected(() => client.SaveAnnotationAsync("A6-DPQG-012/20261118/10", "外观异常", "重新拍摄", new string('x', AnnotationStore.NoteLimit + 1), "李明"), check, "备注超长拒绝标注");
        await ExpectRejected(() => client.SaveAnnotationAsync("A6-DPQG-012/20261118/不存在", "外观异常", "重新拍摄", null, "李明"), check, "不存在的对线文件夹拒绝标注");
        await ExpectRejected(() => client.SaveAnnotationAsync("../outside", "外观异常", "重新拍摄", null, "李明"), check, "路径穿越拒绝标注");
        check((await client.ListAnnotationsAsync("A6-DPQG-012/20261118")).Length == 3, "被拒绝的提交不会新增或覆盖标注");
        const string batch = "A6-DPQG-012/20261118/9";
        const string otherBatch = "A6-DPQG-012/20261118/10";
        await client.SaveAnnotationAsync(otherBatch, "外观异常", "重新拍摄", "其他批次", "李明", "a.png");
        await client.SaveAnnotationAsync(batch, "外观异常", "重新拍摄", "旧版批次记录", "李明");
        var removed = await client.CallAsync(new("annotation-delete", batch, ImageName: "a.png"));
        check(removed.Total == 2 && File.Exists(Path.Combine(folder, "a.png")), "撤销一次清除同一图片的所有标注并保留源文件");
        history = await client.GetAnnotationHistoryAsync(batch);
        check(history.Length == 2 && history.Any(item => item.ImageName == "b.png") && history.Any(item => item.ImageName is null)
            && (await client.GetAnnotationHistoryAsync(otherBatch)).Single().ImageName == "a.png", "撤销只影响当前图片，不影响同批其他图片、历史批次记录或其他批次同名图片");
        await ExpectRejected(() => client.CallAsync(new("annotation-delete", batch)), check, "撤销标注必须指定具体图片");
        await ExpectRejected(() => client.CallAsync(new("annotation-delete", batch, ImageName: "../a.png")), check, "撤销标注拒绝带路径的图片名");
        check((await client.CallAsync(new("annotation-delete", batch, ImageName: "a.png"))).Total == 0, "重复撤销不存在的记录不会误删");

        const string date616 = "DS616/A6-DPQG-055/SaveImages/20261115";
        const string otherDate616 = "DS616/A6-DPQG-056/SaveImages/20261115";
        const string batch616 = date616 + "/9";
        const string otherBatch616 = otherDate616 + "/9";
        Directory.CreateDirectory(Path.Combine(root, batch616));
        Directory.CreateDirectory(Path.Combine(root, otherBatch616));
        var saved616 = await client.SaveAnnotationAsync(batch616, "外观异常", "重新拍摄", null, "李明", "a.png");
        check(saved616 is { EquipmentNo: "A6-DPQG-055", EquipmentType: "DS616", BatchNumber: "9", FolderPath: batch616 }
            && saved616.AutoLineDate == new DateTime(2026, 11, 15), "DS616 截图目录跳过 SaveImages，正确保存机台、机型和日期");
        await client.SaveAnnotationAsync(otherBatch616, "外观异常", "重新拍摄", null, "李明", "a.png");
        check((await client.ListAnnotationsAsync(date616)).Single().EquipmentNo == "A6-DPQG-055"
            && (await client.GetAnnotationHistoryAsync(batch616)).Single().EquipmentType == "DS616",
            "DS616 按天和批次查询都使用正确机台编号");
        check((await client.CallAsync(new("annotation-delete", batch616, ImageName: "a.png"))).Total == 1
            && (await client.GetAnnotationHistoryAsync(batch616)).Length == 0
            && (await client.ListAnnotationsAsync(otherDate616)).Single().EquipmentNo == "A6-DPQG-056",
            "DS616 撤销不影响同日期、同批次的其他机台");

        const string newer616 = "DS616/A6-DPQG-055/SaveImages/20261116/9";
        Directory.CreateDirectory(Path.Combine(root, newer616));
        Directory.CreateDirectory(Path.Combine(root, date616, "10"));
        await client.SaveAnnotationAsync(newer616, "外观异常", "重新拍摄", null, "李明", "a.png");
        var rangeStart = new DateTime(2026, 11, 15);
        var rangeEnd = rangeStart.AddDays(1);
        server.NgBatchQuery = (equipment, start, end, _) =>
        {
            if (equipment != "A6-DPQG-055" || start != rangeStart || end != rangeEnd.AddDays(1))
                throw new Exception("NG 总览应按机台和完整日期区间批量查询");
            return Task.FromResult<RemoteBatchKey[]>([new(start, "9"), new(end.AddDays(-1), "9"), new(start, "99")]);
        };
        var overview = await client.GetNgOverviewAsync("DS616/A6-DPQG-055", rangeStart, rangeEnd);
        check(overview.Length == 2 && overview[0].Batches.Single() is { Path: newer616, Annotated: true }
            && overview[1].Batches.Single() is { Path: batch616, Annotated: false },
            "NG 总览识别 DS616、按日期区分同编号批次且仅列出现存的 NG 文件夹");
        await client.SaveAnnotationAsync(batch616, "外观异常", "重新拍摄", null, "李明", "a.png");
        check((await client.GetNgOverviewAsync("DS616/A6-DPQG-055/SaveImages", rangeStart, rangeEnd))
            .All(day => day.Batches.Single().Annotated), "NG 总览刷新后反映最新标注状态，支持 SaveImages 入口");
        await ExpectRejected(() => client.GetNgOverviewAsync("DS616/A6-DPQG-055", rangeStart, rangeStart.AddDays(31)),
            check, "拒绝超过 31 天的 NG 总览查询");

        var addedCategory = await client.CallAsync(new("annotation-dictionary-add", "category", Text: "  外观复核 A  "));
        var addedPlan = await client.CallAsync(new("annotation-dictionary-add", "plan", Text: "现场复核"));
        check(addedCategory.Dictionary is { } categoryResult && categoryResult.Categories.Count(item => item.Name == "外观复核 A") == 1
            && categoryResult.Plans.Length == 2 && addedPlan.Dictionary is { } planResult && planResult.Plans.Any(item => item.Name == "现场复核"),
            "新增字典项后返回完整字典并保持两组独立");
        await client.CallAsync(new("annotation-dictionary-add", "category", Text: "外观复核 a"));
        var persistedDictionary = await new JsonAnnotationRepository(repositoryPath).GetDictionaryAsync();
        check(persistedDictionary.Categories.Length == 3,
            "自定义字典持久化且同名请求不产生重复项");
        check(persistedDictionary.Categories.Single(item => item.Name == "外观复核 A").Code == "外观复核 A"
            && persistedDictionary.Plans.Single(item => item.Name == "现场复核").Code == "现场复核", "新字典项的值与名称一致");
        var customAnnotation = await client.SaveAnnotationAsync(batch, "外观复核 A", "现场复核", null, "李明", "a.png");
        check(customAnnotation.CategoryName == "外观复核 A" && customAnnotation.PlanName == "现场复核", "新增字典项可立即用于提交标注");
        check(persistedDictionary.Remarks.Length == 0, "旧字典数据兼容空备注标签");
        await client.AddAnnotationDictionaryItemAsync(AnnotationDictionaryKind.Remark, "  已通知设备  ");
        await client.AddAnnotationDictionaryItemAsync(AnnotationDictionaryKind.Remark, "已通知设备");
        var remarks = await new JsonAnnotationRepository(repositoryPath).GetDictionaryAsync();
        check(remarks.Remarks is [{ Code: "已通知设备", Name: "已通知设备" }]
            && remarks.Categories.Length == persistedDictionary.Categories.Length
            && remarks.Plans.Length == persistedDictionary.Plans.Length,
            "备注标签单独持久化、名称和值一致、去重且不影响归类方案");
        var refreshedRemarks = await client.GetAnnotationDictionaryAsync();
        check(refreshedRemarks.Remarks.Single().Name == "已通知设备", "新增备注标签后字典缓存立即刷新");
        await new JsonAnnotationRepository(repositoryPath).AddDictionaryItemAsync(AnnotationDictionaryKind.Remark, "其他节点新增");
        var externallyUpdated = await client.GetAnnotationDictionaryAsync();
        check(externallyUpdated.Remarks.Any(item => item.Name == "其他节点新增"),
            "其他服务端修改字典后重新读取立即可见，不返回十分钟旧缓存");
        var remarkAnnotation = await client.SaveAnnotationAsync(batch, "外观复核 A", "现场复核", "手写内容；已通知设备", "李明", "a.png");
        check(remarkAnnotation.Note == "手写内容；已通知设备", "备注标签内容沿用备注字段保存");
        await ExpectRejected(() => client.CallAsync(new("annotation-dictionary-add", "category", Text: "   ")), check, "拒绝空白字典名称");
        await ExpectRejected(() => client.CallAsync(new("annotation-dictionary-add", "other", Text: "不受支持")), check, "拒绝未知字典分组");
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
