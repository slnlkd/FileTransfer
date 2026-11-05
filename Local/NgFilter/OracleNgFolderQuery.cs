using System.Globalization;
using System.IO;
using System.Text.Json;
using Oracle.ManagedDataAccess.Client;
using Serilog;
using Transfer.Core;
using Transfer.Core.Remote;

namespace RemoteFiles.Server;

internal sealed record NgFilterSettings(string ConnectionString, int CommandTimeoutSeconds = 10);

/// <summary>
/// 个性化 NG 截图过滤：按机台编号和日期查询 Oracle，只保留 NG 对应的文件夹。
/// 该文件属于本机配置，不纳入版本控制。
/// </summary>
internal sealed class OracleNgFolderQuery
{
    private const string FilterName = "ng";
    private readonly NgFilterSettings settings;
    private OracleNgFolderQuery(NgFilterSettings settings) => this.settings = settings;

    public static OracleNgFolderQuery? Load()
    {
        string path = AppLogging.GetSettingsPath("ngfilter.json");
        if (!File.Exists(path)) { Log.Warning("只看NG配置不存在；路径 {SettingsPath}", path); return null; }
        var settings = JsonSerializer.Deserialize<NgFilterSettings>(File.ReadAllText(path));
        if (string.IsNullOrWhiteSpace(settings?.ConnectionString))
        {
            Log.Warning("只看NG配置缺少连接字符串；路径 {SettingsPath}", path);
            return null;
        }
        Log.Information("只看NG配置已加载；路径 {SettingsPath}", path);
        return new(settings);
    }

    public async Task<RemoteFilterResult> ApplyAsync(string filter, string path, RemoteEntry[] entries)
    {
        if (!filter.Equals(FilterName, StringComparison.OrdinalIgnoreCase)) return new(entries);
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !DateTime.TryParseExact(parts[^1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new(entries);
        // 机台目录与日期目录之间允许存在 SaveImages 等中间层级。
        int index = Array.FindLastIndex(parts, parts.Length - 2, parts.Length - 1,
            part => part.StartsWith("A6", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return new(entries);
        string equipmentNo = parts[index];
        var matches = await FindAsync(equipmentNo, DateTime.SpecifyKind(date.Date, DateTimeKind.Utc));
        return new(entries.Where(e => e.IsDirectory && matches.Contains(e.Name.Trim())).ToArray());
    }

    private async Task<HashSet<string>> FindAsync(string equipmentNo, DateTime utcDate)
    {
        const string sql = "SELECT DISTINCT \"Parameter2\" FROM \"RCMDB\".\"ProjectRpaAutoAlignmentResults\" " +
            "WHERE \"EquipmentNo\" = :equipmentNo AND \"AlignmentResult\" = :result " +
            "AND \"AlignmentStartTime\" >= :startUtc AND \"AlignmentStartTime\" < :endUtc AND \"Parameter2\" IS NOT NULL";
        await using var connection = new OracleConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        command.CommandTimeout = Math.Clamp(settings.CommandTimeoutSeconds, 1, 60);
        command.Parameters.Add("equipmentNo", OracleDbType.NVarchar2, 50).Value = equipmentNo;
        command.Parameters.Add("result", OracleDbType.NVarchar2, 50).Value = "N";
        command.Parameters.Add("startUtc", OracleDbType.TimeStamp).Value = DateTime.SpecifyKind(utcDate.Date, DateTimeKind.Unspecified);
        command.Parameters.Add("endUtc", OracleDbType.TimeStamp).Value = DateTime.SpecifyKind(utcDate.Date.AddDays(1), DateTimeKind.Unspecified);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0)) continue;
            string value = reader.GetString(0).Trim();
            if (value.Length > 0) values.Add(value);
        }
        Log.Information("只看NG查询完成；机台 {EquipmentNo}，日期 {Date}，NG 文件夹 {Count}", equipmentNo, utcDate.ToString("yyyyMMdd"), values.Count);
        return values;
    }
}
