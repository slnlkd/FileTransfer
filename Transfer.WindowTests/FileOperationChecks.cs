extern alias RemoteClient;
using System.IO;
using Transfer.Core.Remote;
using Operations = RemoteClient::RemoteFiles.Desktop.RemoteFileOperationService;

internal static class FileOperationChecks
{
    public static async Task RunAsync(string temp)
    {
        string root = Path.Combine(temp, "operation-server"), local = Path.Combine(temp, "operation-local");
        Directory.CreateDirectory(root); Directory.CreateDirectory(local);
        string file = Path.Combine(local, "test.txt"); await File.WriteAllTextAsync(file, "new");
        await File.WriteAllTextAsync(Path.Combine(root, "test.txt"), "old");
        await using var server = new RemoteFileService(root); server.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", server.Port); var service = new Operations();
        int result = await service.UploadFilesAsync(endpoint, [file], "", _ => true, _ => false, (_, _) => { }, default);
        if (result != 0 || await File.ReadAllTextAsync(Path.Combine(root, "test.txt")) != "old") throw new Exception("拒绝覆盖不能修改远程文件");
        result = await service.UploadFilesAsync(endpoint, [file], "", _ => true, _ => true, (_, _) => { }, default);
        if (result != 1 || await File.ReadAllTextAsync(Path.Combine(root, "test.txt")) != "new") throw new Exception("允许覆盖后应上传文件");
        string folder = Path.Combine(local, "folder"); Directory.CreateDirectory(Path.Combine(folder, "child"));
        await File.WriteAllTextAsync(Path.Combine(folder, "child", "nested.txt"), "nested");
        await service.UploadPathsAsync(endpoint, [folder], "", _ => true, _ => { }, (_, _) => { }, default);
        if (await File.ReadAllTextAsync(Path.Combine(root, "folder", "child", "nested.txt")) != "nested") throw new Exception("拖入上传应保留子目录");
        var entry = (await endpoint.CallAsync(new("list"))).Entries!.Single(item => item.Name == "folder");
        string zip = await service.CompressAsync(endpoint, entry, new Progress<RemoteTransferProgress>(), default);
        var archive = (await endpoint.CallAsync(new("list"))).Entries!.Single(item => item.Path == zip);
        string destination = Path.Combine(temp, "download.zip");
        await service.DownloadAsync(endpoint, archive, destination, new Progress<RemoteTransferProgress>(), default);
        if (!File.Exists(destination)) throw new Exception("归档下载未生成文件");
        await service.DeleteAsync(endpoint, archive, default);
        if (File.Exists(Path.Combine(root, zip))) throw new Exception("删除归档失败");
        Console.WriteLine("通过：文件操作服务覆盖决策、嵌套上传、压缩下载及删除");
    }
}
