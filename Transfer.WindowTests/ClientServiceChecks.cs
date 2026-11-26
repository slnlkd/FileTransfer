extern alias RemoteClient;
using System.IO;
using Settings = RemoteClient::RemoteFiles.Client.EndpointSettingsService;
using Update = RemoteClient::RemoteFiles.Client.ClientUpdateService;

internal static class ClientServiceChecks
{
    public static async Task RunAsync(string temp)
    {
        string path = Path.Combine(temp, "endpoint-service.json");
        await File.WriteAllTextAsync(path, "{\"Host\":\"localhost\",\"Port\":9501}");
        var service = new Settings(path);
        var old = service.Load();
        if (old.Endpoint != "localhost:9501" || !old.Recent.Contains("localhost:9501")) throw new Exception("旧版连接配置迁移失败");
        await service.SaveAsync("::1", 9501, ["[::1]:9501"]);
        var saved = service.Load();
        if (saved.Endpoint != "[::1]:9501" || !Settings.TryParseEndpoint(saved.Endpoint, out var host, out var port) || host != "::1" || port != 9501)
            throw new Exception("IPv6 地址和最近连接未正确保存恢复");
        if (Settings.TryParseEndpoint("10.100.40.181:65536", out _, out _) || Settings.TryParseEndpoint("missing-port", out _, out _))
            throw new Exception("非法连接地址应被拒绝");
        if (!Update.IsNewer("1.6.16", new Version(1, 6, 9)) || Update.IsNewer("1.6.15", new Version(1, 6, 16))
            || Update.IsNewer("invalid", new Version(1, 6, 16))) throw new Exception("客户端更新版本比较错误");
        Console.WriteLine("通过：连接旧配置、IPv6 与端口验证、最近连接保存及更新版本比较");
    }
}
