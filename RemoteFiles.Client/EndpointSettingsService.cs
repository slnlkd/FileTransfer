using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace RemoteFiles.Client;

public sealed class EndpointSettingsService(string settingsPath)
{
    public static readonly string[] PresetEndpoints = Enumerable.Range(181, 3).Select(address => $"10.100.40.{address}:9501").ToArray();
    public string Path => settingsPath;
    private sealed record Settings(string Host, int Port, string[]? RecentEndpoints = null);
    public (string Endpoint, string[] Recent) Load()
    {
        string endpoint = PresetEndpoints[0]; string[] recent = [];
        try
        {
            if (File.Exists(settingsPath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) is { } saved)
            {
                string previous = FormatEndpoint(saved.Host ?? "", saved.Port);
                if (TryParseEndpoint(previous, out _, out _)) endpoint = previous;
                recent = (saved.RecentEndpoints ?? []).Prepend(previous).Where(value => TryParseEndpoint(value, out _, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "读取远程文件客户端配置失败"); }
        return (endpoint, recent);
    }
    public async Task SaveAsync(string host, int port, string[] recent)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new Settings(host, port, recent)));
    }
    public static string FormatEndpoint(string host, int port) =>
        host.Contains(':') ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";

    public static bool TryParseEndpoint(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        string endpoint = text?.Trim() ?? "";
        int separator = endpoint.LastIndexOf(':');
        if (separator <= 0) return false;

        string address = endpoint[..separator].Trim();
        if (address.StartsWith('[') && address.EndsWith(']'))
        {
            host = address[1..^1];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            host = address;
            if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        }
        return int.TryParse(endpoint[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }
}
