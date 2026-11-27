using System.Text.Json;
using Transfer.Core.Remote;

internal static class BatchIdentityChecks
{
    public static async Task RunAsync(string testRoot, Action<bool, string> check)
    {
        check(BatchNames.Normalize(" 181_3 ") == "181_3" && BatchNames.Normalize("182_3") != BatchNames.Normalize("3"),
            "批次匹配必须保留完整前缀，只去首尾空白");
        string root = Path.Combine(testRoot, "batch-identity");
        const string datePath = "A6-DPQG-072/20261125";
        string[] names = ["3", "181_3", "182_3", "183_3", "181_4"];
        foreach (string name in names) Directory.CreateDirectory(Path.Combine(root, datePath, name));
        string json = Path.Combine(testRoot, "batch-identity.json");
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new
        {
            dictionary = new RemoteDictionary([new("归类", "归类")], [new("方案", "方案")]),
            annotations = Array.Empty<RemoteAnnotation>()
        }));
        var repository = new JsonAnnotationRepository(json);
        await using var service = new RemoteFileService(root, repository);
        service.Start(0);
        var client = new RemoteFileClient("127.0.0.1", service.Port);
        check(await client.GetAnnotationTargetAsync(datePath + "/3") is null, "JSON 标注服务保留现有写入能力");
        var date = new DateTime(2026, 11, 25);
        foreach (string ngName in new[] { "3", "181_3", "182_3" })
        {
            service.NgBatchQuery = (_, _, _, _) => Task.FromResult<RemoteBatchKey[]>([new(date, ngName)]);
            var days = await client.GetNgOverviewAsync("A6-DPQG-072", date, date);
            check(days.Single().Batches.Select(item => item.Name).SequenceEqual([ngName]),
                $"NG 为 {ngName} 时不得带出同后缀的其他批次");
        }
        foreach (string name in names.Take(3))
            await client.SaveAnnotationAsync(datePath + "/" + name, "归类", "方案", name, "测试员", "same.png");
        foreach (string name in names.Take(3))
            check((await client.GetAnnotationHistoryAsync(datePath + "/" + name)) is [{ Note: var note }] && note == name,
                $"批次 {name} 的同名图片历史不能串到其他前缀");
        service.NgBatchQuery = (_, _, _, _) => Task.FromResult<RemoteBatchKey[]>(names.Select(name => new RemoteBatchKey(date, name)).ToArray());
        var all = (await client.GetNgOverviewAsync("A6-DPQG-072", date, date)).Single().Batches;
        check(all.Where(item => item.Annotated).Select(item => item.Name).ToHashSet().SetEquals(names.Take(3)),
            "NG 标注状态只能点亮完整批次编号匹配的目录");
        await client.DeleteImageAnnotationsAsync(datePath + "/181_3", "same.png");
        check((await client.GetAnnotationHistoryAsync(datePath + "/181_3")).Length == 0
            && (await client.GetAnnotationHistoryAsync(datePath + "/3")).Length == 1
            && (await client.GetAnnotationHistoryAsync(datePath + "/182_3")).Length == 1,
            "撤销前缀批次时不能删除同后缀其他目录的记录");
        await repository.SaveAsync(new(datePath + "/183_3", "A6-DPQG-072", AutoLineDate: date,
            BatchNumber: " 183_3 ", ImageName: "same.png", Note: "空格批次"));
        check((await client.GetAnnotationHistoryAsync(datePath + "/183_3")) is [{ Note: "空格批次" }],
            "批次号首尾空格不影响完整编号的历史查询");
        check(await client.DeleteImageAnnotationsAsync(datePath + "/183_3", "same.png") == 1,
            "批次号首尾空格不影响撤销当前图片");

        var linked = new LinkedRepository(repository);
        await using var linkedService = new RemoteFileService(root, linked);
        linkedService.Start(0);
        var linkedClient = new RemoteFileClient("127.0.0.1", linkedService.Port);
        string linkedFolder = datePath + "/181_3";
        var target = await linkedClient.GetAnnotationTargetAsync(linkedFolder);
        check(target is { ResultId: "181_3", Error: null } && linked.LastBatch == "181_3",
            "标注目标按完整批次定位并经协议返回结果 ID");
        check(await linkedClient.GetAnnotationTargetAsync(datePath + "/3") is { ResultId: null, Error: not null },
            "没有 NG 结果时返回阻止写入的原因");
        check(await linkedClient.GetAnnotationTargetAsync(datePath + "/182_3") is { ResultId: "182_3-latest", Error: null },
            "多条 NG 对应的批次通过协议返回最新结果 ID");
        await linkedClient.SaveAnnotationAsync(linkedFolder, "归类", "方案", "", "测试员", "same.png", ngResultId: target!.ResultId);
        check(linked.LastWrite == (true, "181_3"), "提交将打开窗口时取得的结果 ID 传给仓储");
        await linkedClient.DeleteImageAnnotationsAsync(linkedFolder, "same.png", ngResultId: target.ResultId);
        check(linked.LastWrite == (true, "181_3"), "撤销将打开窗口时取得的结果 ID 传给仓储");
        await linkedClient.SaveAnnotationAsync(datePath + "/3", "归类", "方案", "", "测试员", "other.png");
        check(linked.LastWrite == (false, null), "旧客户端协议允许普通标注并由仓储补查结果");
        const string categoryId = "bc08253a47cabdadf4bb982ace184203";
        int dictionaryReads = linked.DictionaryReads;
        await linkedClient.SaveAnnotationAsync(linkedFolder, "归类", "方案", "", "测试员", "selected.png",
            ngResultId: target.ResultId, categoryDictionaryId: categoryId);
        check(linked.LastWrite == (true, "181_3") && linked.LastCategoryId == categoryId && linked.DictionaryReads == dictionaryReads,
            "携带两个 ID 时直接交给联动仓储且不重新加载整套字典");

        int historyCount = (await client.GetAnnotationHistoryAsync(datePath + "/3")).Length;
        foreach (var request in new[]
        {
            new RemoteRequest("annotation-add", datePath + "/3", CategoryCode: "归类", PlanCode: "方案",
                Operator: "测试员", ImageName: "other.png") { NgResultId = "previous-oracle-result" },
            new RemoteRequest("annotation-delete", datePath + "/3", ImageName: "other.png") { RequireNgResult = true }
        })
        {
            bool rejected = false;
            try { await client.CallAsync(request); }
            catch (IOException ex) when (ex.Message.Contains("Oracle", StringComparison.Ordinal)) { rejected = true; }
            check(rejected && (await client.GetAnnotationHistoryAsync(datePath + "/3")).Length == historyCount,
                $"JSON 仓储拒绝要求 Oracle 联动的 {request.Command}，且保留原标注");
        }
    }

    private sealed class LinkedRepository(JsonAnnotationRepository inner) : IAnnotationRepository, INgAnnotationRepository
    {
        public (bool Required, string? Id) LastWrite { get; private set; }
        public string? LastBatch { get; private set; }
        public string? LastCategoryId { get; private set; }
        public int DictionaryReads { get; private set; }
        public Task<string?> FindResultIdAsync(string equipmentNo, DateTime date, string batch, CancellationToken ct)
        {
            LastBatch = batch;
            return Task.FromResult<string?>(batch switch
            {
                "181_3" => batch,
                "182_3" => "182_3-latest",
                _ => null
            });
        }
        public Task<RemoteAnnotation[]> ListAsync(string equipmentNo, DateTime date, CancellationToken ct, string? batch)
            => inner.ListAsync(equipmentNo, date, ct, batch);
        public Task<RemoteBatchKey[]> ListAnnotatedBatchesAsync(string equipmentNo, DateTime start, DateTime end, CancellationToken ct)
            => inner.ListAnnotatedBatchesAsync(equipmentNo, start, end, ct);
        public Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct)
        {
            DictionaryReads++;
            return inner.GetDictionaryAsync(ct);
        }
        public Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct)
            => inner.AddDictionaryItemAsync(kind, name, ct);
        public Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, CancellationToken ct) => inner.SaveAsync(annotation, ct);
        public Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName, CancellationToken ct)
            => inner.DeleteImageAsync(equipmentNo, date, batch, folderPath, imageName, ct);
        public Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, bool required, string? id, CancellationToken ct)
        {
            LastWrite = (required, id);
            LastCategoryId = annotation.CategoryDictionaryId;
            return SaveAsync(annotation, ct);
        }
        public Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName,
            bool required, string? id, CancellationToken ct)
        {
            LastWrite = (required, id);
            return DeleteImageAsync(equipmentNo, date, batch, folderPath, imageName, ct);
        }
    }
}
