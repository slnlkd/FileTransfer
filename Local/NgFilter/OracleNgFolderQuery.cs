using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Server;

/// <summary>
/// 个性化 NG 截图过滤：按机台编号和日期查询 Oracle，只保留 NG 对应的文件夹。
/// 源码纳入版本控制；数据库连接配置单独部署。
/// </summary>
internal sealed class OracleNgFolderQuery
{
    private const string FilterName = "ng";
    private readonly NgFilterSettings settings;
    internal OracleNgFolderQuery(NgFilterSettings settings) => this.settings = settings;

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
        var matches = await FindBatchesAsync(equipmentNo, date.Date, date.Date.AddDays(1), CancellationToken.None);
        return new(BatchNames.FilterDirectories(entries, matches.Select(item => item.Batch)));
    }

    public async Task<RemoteBatchKey[]> FindBatchesAsync(string equipmentNo, DateTime start, DateTime end, CancellationToken ct)
    {
        string sql = OracleAnnotationSql.SelectNgBatches;
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
