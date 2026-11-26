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
    }
}
