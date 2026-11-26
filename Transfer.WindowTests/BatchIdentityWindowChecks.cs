extern alias RemoteClient;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Transfer.Core.Remote;
using Browser = RemoteClient::RemoteFiles.Desktop.RemoteBrowser;

internal static partial class RemoteWindowChecks
{
    private static async Task CheckBatchIdentityAsync(string temp)
    {
        const string equipment = "A6-DPQG-072";
        const string datePath = equipment + "/20261125";
        string root = Path.Combine(temp, "batch-identity-ui");
        string[] names = ["3", "181_3", "182_3"];
        foreach (string name in names) Directory.CreateDirectory(Path.Combine(root, datePath, name));
        string json = Path.Combine(temp, "batch-identity-ui.json");
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new
        {
            dictionary = new RemoteDictionary([new("归类", "归类")], [new("方案", "方案")]),
            annotations = Array.Empty<RemoteAnnotation>()
        }));
        await using var service = new RemoteFileService(root, new JsonAnnotationRepository(json));
        service.ListFilter = (_, path, rows) => Task.FromResult(new RemoteFilterResult(path == datePath
            ? BatchNames.FilterDirectories(rows, names) : rows));
        service.NgBatchQuery = (_, _, _, _) => Task.FromResult(names.Select(name => new RemoteBatchKey(new(2026, 11, 25), name)).ToArray());
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        await endpoint.SaveAnnotationAsync(datePath + "/181_3", "归类", "方案", "只标注此前缀", "测试员", "same.png");
        var browser = new Browser(); var host = new Window { Content = browser, Width = 1100, Height = 700 };
        Prepare(host); host.Show();
        try
        {
            await browser.ConnectAsync(endpoint);
            typeof(Browser).GetMethod("SetLocalFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(browser, ["ng"]);
            await WaitAsync(() => !browser.IsBusy);
            await browser.OpenAsync(new("20261125", datePath, true, 0, default, default));
            var rows = ((DataGrid)browser.FindName("FileGrid")).Items.Cast<RemoteEntry>().ToDictionary(item => item.Name);
            if (rows.Count != 3 || rows["181_3"].AnnotationStatus != "已标注"
                || rows["3"].AnnotationStatus != "未标注" || rows["182_3"].AnnotationStatus != "未标注")
                throw new Exception("日期目录的标注状态不能串到同后缀的其他批次");
            Console.WriteLine("通过：日期列表按完整批次号显示标注状态，纯编号及不同前缀互不串标");
        }
        finally { host.Close(); }
    }
}
