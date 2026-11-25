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
        var matches = (await FindBatchesAsync(equipmentNo, date.Date, date.Date.AddDays(1), CancellationToken.None))
            .Select(item => BatchNames.Normalize(item.Batch)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 目录名可能是 "181_53"，数据库里可能存 "53"（或反过来），按编号比较避免过渡期匹配不到。
        return new(entries.Where(e => e.IsDirectory && matches.Contains(BatchNames.Normalize(e.Name))).ToArray());
    }

    public async Task<RemoteBatchKey[]> FindBatchesAsync(string equipmentNo, DateTime start, DateTime end, CancellationToken ct)
    {
        const string sql = "SELECT DISTINCT TRUNC(\"AlignmentStartTime\"), \"Parameter2\" FROM \"RCMDB\".\"ProjectRpaAutoAlignmentResults\" " +
            "WHERE \"EquipmentNo\" = :equipmentNo AND \"AlignmentResult\" = :result " +
            "AND \"AlignmentStartTime\" >= :startUtc AND \"AlignmentStartTime\" < :endUtc AND \"Parameter2\" IS NOT NULL";
        await using var connection = new OracleConnection(settings.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        command.CommandTimeout = Math.Clamp(settings.CommandTimeoutSeconds, 1, 60);
        command.Parameters.Add("equipmentNo", OracleDbType.NVarchar2, 50).Value = equipmentNo;
        command.Parameters.Add("result", OracleDbType.NVarchar2, 50).Value = "N";
        command.Parameters.Add("startUtc", OracleDbType.TimeStamp).Value = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        command.Parameters.Add("endUtc", OracleDbType.TimeStamp).Value = DateTime.SpecifyKind(end, DateTimeKind.Unspecified);
        var values = new List<RemoteBatchKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1)) continue;
            string value = reader.GetString(1).Trim();
            if (value.Length > 0) values.Add(new(reader.GetDateTime(0), value));
        }
        Log.Information("NG 查询完成；机台 {EquipmentNo}，开始 {Start}，截止 {End}，NG 批次 {Count}", equipmentNo, start, end, values.Count);
        return values.ToArray();
    }
}
