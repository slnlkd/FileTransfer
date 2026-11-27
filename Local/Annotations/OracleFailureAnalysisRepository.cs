using System.Collections.Concurrent;
using System.IO;
using Oracle.ManagedDataAccess.Client;
using Serilog;
using Transfer.Core;
using Transfer.Core.Remote;

namespace RemoteFiles.Server;

/// <summary>
/// 标注（对线失败原因分析）的 Oracle 仓储。
/// 表名在运行时从 all_tables 解析（兼容全大写/混合大小写、不同 schema），
/// 字典读 RpaDataItems(ItemCode=ExceptionType / ExceptionSol) + RpaDataItemDetails。
/// </summary>
internal sealed partial class OracleFailureAnalysisRepository : IAnnotationRepository, INgAnnotationRepository
{
    internal const string CategoryItemCode = "ExceptionType";
    internal const string PlanItemCode = "ExceptionSol";
    internal const string RemarkItemCode = "ExceptionRemark";
    internal const string TargetTable = "AUTOLINEFAILUREANALYSISLOG";
    private static readonly ConcurrentDictionary<string, string?> equipmentTypes = new(StringComparer.OrdinalIgnoreCase);

    private readonly string connectionString;
    private readonly int commandTimeout;
    private string? resolvedTable;
    internal string? ResolvedLogTable => resolvedLogTableName;
    private string? resolvedLogTableName;

    internal OracleFailureAnalysisRepository(NgFilterSettings settings)
    {
        connectionString = settings.ConnectionString;
        commandTimeout = settings.AnnotationTimeout;
    }

    public async Task<RemoteAnnotation[]> ListAsync(string equipmentNo, DateTime date, CancellationToken ct = default, string? batch = null)
    {
        await using var connection = await OpenAsync(ct);
        string sql = OracleAnnotationSql.SelectHistory(await TableAsync(connection, ct), batch);
        await using var command = Create(connection, sql);
        command.Parameters.Add("pEqp", OracleDbType.NVarchar2, 32).Value = equipmentNo;
        command.Parameters.Add("pDate", OracleDbType.Date).Value = date;
        if (batch is not null)
        {
            command.Parameters.Add("pBatch", OracleDbType.NVarchar2, 32).Value = BatchNames.Normalize(batch);
        }
        var items = new List<RemoteAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (storedFolder, storedImage) = SplitFileDirectory(reader.IsDBNull(5) ? "" : reader.GetString(5));
            items.Add(new RemoteAnnotation(
                storedFolder,
                equipmentNo, Text(reader, 7), date, Text(reader, 0),
                Text(reader, 1), Text(reader, 2), Text(reader, 6), Text(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4), storedImage));
        }
        return items.ToArray();
    }

    public async Task<RemoteBatchKey[]> ListAnnotatedBatchesAsync(string equipmentNo, DateTime start, DateTime end, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        string sql = OracleAnnotationSql.SelectAnnotatedBatches(await TableAsync(connection, ct));
        await using var command = Create(connection, sql);
        command.Parameters.Add("pEqp", OracleDbType.NVarchar2, 32).Value = equipmentNo;
        command.Parameters.Add("pStart", OracleDbType.Date).Value = start;
        command.Parameters.Add("pEnd", OracleDbType.Date).Value = end;
        var batches = new List<RemoteBatchKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            batches.Add(new(reader.GetDateTime(0), reader.GetString(1).Trim()));
        return batches.ToArray();
    }

    /// <summary>FILE_DIRECTORY 里可能带图片名，读出来时把文件夹和图片名拆开。</summary>
    private static (string Folder, string? Image) SplitFileDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ("", null);
        string normalized = value.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        if (slash <= 0) return (normalized, null);
        string name = normalized[(slash + 1)..];
        return Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".png" ? (normalized[..slash], name) : (normalized, null);
    }

    public Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, CancellationToken ct = default)
        => SaveAsync(annotation, false, null, ct);

    public async Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, bool requireNgResult, string? resultId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        string table = await TableAsync(connection, ct);
        // 优先使用设备主数据；DS616 路径可在主数据缺失时提供机型。
        string? registeredType = await ResolveEquipmentTypeAsync(connection, annotation.EquipmentNo ?? "", ct);
        string equipmentType = string.IsNullOrWhiteSpace(registeredType) ? annotation.EquipmentType ?? "未知" : registeredType;
        await using var transaction = (OracleTransaction)await connection.BeginTransactionAsync(ct);
        ResultTarget? target = null;
        if (resultId is not null && annotation.CategoryDictionaryId is { } dictionaryId)
            await UpdateSelectedResultAsync(connection, transaction, annotation, resultId, dictionaryId, ct);
        else
            target = await FindWriteTargetAsync(connection, transaction, annotation.EquipmentNo ?? "",
                annotation.AutoLineDate ?? DateTime.Today, annotation.BatchNumber ?? "", requireNgResult, resultId, ct);
        string sql = OracleAnnotationSql.InsertAnnotation(table);
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        // Oracle DATE 精确到秒，同一秒内新增的记录用带时间的 ID 决定读取顺序。
        command.Parameters.Add("pId", OracleDbType.NVarchar2, 128).Value = Guid.CreateVersion7().ToString("N");
        command.Parameters.Add("pEqp", OracleDbType.NVarchar2, 32).Value = annotation.EquipmentNo ?? "";
        command.Parameters.Add("pEqpType", OracleDbType.NVarchar2, 32).Value = equipmentType;
        command.Parameters.Add("pDate", OracleDbType.Date).Value = annotation.AutoLineDate ?? DateTime.Today;
        command.Parameters.Add("pBatch", OracleDbType.NVarchar2, 32).Value = annotation.BatchNumber ?? "";
        command.Parameters.Add("pUser", OracleDbType.Varchar2, 64).Value = annotation.Operator ?? "";
        // FILE_DIRECTORY 记录到具体图片：对线文件夹/图片名
        string fileDirectory = string.IsNullOrWhiteSpace(annotation.ImageName)
            ? annotation.FolderPath
            : annotation.FolderPath.TrimEnd('/') + "/" + annotation.ImageName;
        // 实际表里这三列是 VARCHAR2（不是 NVARCHAR2），按列类型绑定。
        command.Parameters.Add("pType", OracleDbType.Varchar2, 3072).Value = annotation.CategoryName ?? "";
        command.Parameters.Add("pPlan", OracleDbType.Varchar2, 3072).Value = annotation.PlanName ?? "";
        command.Parameters.Add("pDir", OracleDbType.Varchar2, 3072).Value = fileDirectory;
        command.Parameters.Add("pNote", OracleDbType.Varchar2, 3072).Value = annotation.Note ?? "";
        try
        {
            await command.ExecuteNonQueryAsync(ct);
            if (target is not null)
                await UpdateResultAsync(connection, transaction, target, annotation.CategoryName, ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (OracleException ex)
        {
            Log.Error(ex, "标注写入数据库失败；Oracle {Code}；SQL {Sql}", ex.Number, sql);
            throw;
        }
        Log.Information("标注已写入数据库；设备 {EquipmentNo}，日期 {Date}，批次 {Batch}，归类 {Type}，方案 {Plan}",
            annotation.EquipmentNo, annotation.AutoLineDate?.ToString("yyyyMMdd"), annotation.BatchNumber, annotation.CategoryName, annotation.PlanName);
        return annotation with { UpdatedAt = DateTime.Now, EquipmentType = equipmentType };
    }

    public Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName, CancellationToken ct = default)
        => DeleteImageAsync(equipmentNo, date, batch, folderPath, imageName, false, null, ct);

    public async Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName,
        bool requireNgResult, string? resultId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        string table = await TableAsync(connection, ct);
        await using var transaction = (OracleTransaction)await connection.BeginTransactionAsync(ct);
        var target = await FindWriteTargetAsync(connection, transaction, equipmentNo, date, batch, requireNgResult, resultId, ct);
        string sql = OracleAnnotationSql.DeleteImageAnnotations(table);
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        command.Parameters.Add("pEqp", OracleDbType.NVarchar2, 32).Value = equipmentNo;
        command.Parameters.Add("pDate", OracleDbType.Date).Value = date;
        command.Parameters.Add("pBatch", OracleDbType.NVarchar2, 32).Value = BatchNames.Normalize(batch);
        command.Parameters.Add("pDir", OracleDbType.Varchar2, 3072).Value = folderPath + "/" + imageName;
        int deleted = await command.ExecuteNonQueryAsync(ct);
        if (target is not null && deleted > 0)
        {
            string? category = await FindLatestCategoryAsync(connection, transaction, table, equipmentNo, date, batch, ct);
            await UpdateResultAsync(connection, transaction, target, category, ct);
        }
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return deleted;
    }

    public async Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadDictionaryAsync(connection, null, ct);
    }

    public async Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct = default)
    {
        string code = kind switch
        {
            AnnotationDictionaryKind.Category => CategoryItemCode,
            AnnotationDictionaryKind.Plan => PlanItemCode,
            AnnotationDictionaryKind.Remark => RemarkItemCode,
            _ => throw new IOException("不支持的标注字典分组")
        };
        await using var connection = await OpenAsync(ct);
        await using var transaction = (OracleTransaction)await connection.BeginTransactionAsync(ct);
        string groupName = kind switch
        {
            AnnotationDictionaryKind.Category => "异常归类",
            AnnotationDictionaryKind.Plan => "异常处理方案",
            _ => "异常备注"
        };
        byte[] groupId = await LockDictionaryGroupAsync(connection, transaction, code, ct)
            ?? await CreateDictionaryGroupAsync(connection, transaction, code, groupName, ct);

        var dictionary = await ReadDictionaryAsync(connection, transaction, ct);
        var items = kind switch
        {
            AnnotationDictionaryKind.Category => dictionary.Categories,
            AnnotationDictionaryKind.Plan => dictionary.Plans,
            _ => dictionary.Remarks
        };
        if (!items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            const string insert = "INSERT INTO \"RCMDB\".\"RpaDataItemDetails\" " +
                "(\"Id\",\"ItemId\",\"ItemName\",\"ItemValue\",\"SortCode\",\"CreationTime\",\"IsDeleted\",\"ExtraProperties\",\"ConcurrencyStamp\") " +
                "SELECT :pId, :pGroup, :pName, :pName, NVL(MAX(\"SortCode\"), 0) + 1, SYS_EXTRACT_UTC(SYSTIMESTAMP), 0, '{}', :pStamp " +
                "FROM \"RCMDB\".\"RpaDataItemDetails\" WHERE \"ItemId\" = :pGroup";
            await using var command = Create(connection, insert);
            command.Transaction = transaction;
            var id = Guid.NewGuid();
            command.Parameters.Add("pId", OracleDbType.Raw, 16).Value = id.ToByteArray();
            command.Parameters.Add("pGroup", OracleDbType.Raw, groupId.Length).Value = groupId;
            command.Parameters.Add("pName", OracleDbType.NVarchar2, 255).Value = name;
            command.Parameters.Add("pStamp", OracleDbType.NVarchar2, 40).Value = id.ToString("N");
            await command.ExecuteNonQueryAsync(ct);
            dictionary = await ReadDictionaryAsync(connection, transaction, ct);
        }
        await transaction.CommitAsync(ct);
        return dictionary;
    }

    private async Task<byte[]?> LockDictionaryGroupAsync(OracleConnection connection, OracleTransaction transaction, string code, CancellationToken ct)
    {
        const string findGroup = "SELECT \"Id\" FROM \"RCMDB\".\"RpaDataItems\" " +
            "WHERE UPPER(TRIM(\"ItemCode\")) = :pCode AND \"IsDeleted\" = 0 FOR UPDATE";
        await using var command = Create(connection, findGroup);
        command.Transaction = transaction;
        command.Parameters.Add("pCode", OracleDbType.NVarchar2, 50).Value = code.ToUpperInvariant();
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        byte[] id = reader.GetOracleBinary(0).Value;
        if (await reader.ReadAsync(ct)) throw new IOException($"数据库存在多个启用的标注字典分组 {code}");
        return id;
    }

    private async Task<byte[]> CreateDictionaryGroupAsync(OracleConnection connection, OracleTransaction transaction, string code, string name, CancellationToken ct)
    {
        // 固定主键让多个服务端同时初始化时只创建一个分组。
        byte[] id = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("FileTransfer:AnnotationDictionary:" + code))[..16];
        const string insertGroup = "INSERT INTO \"RCMDB\".\"RpaDataItems\" " +
            "(\"Id\",\"ItemCode\",\"ItemName\",\"CreationTime\",\"IsDeleted\",\"ExtraProperties\",\"ConcurrencyStamp\") " +
            "SELECT :pId, :pCode, :pName, SYS_EXTRACT_UTC(SYSTIMESTAMP), 0, '{}', :pStamp FROM DUAL " +
            "WHERE NOT EXISTS (SELECT 1 FROM \"RCMDB\".\"RpaDataItems\" WHERE UPPER(TRIM(\"ItemCode\")) = :pUpperCode AND \"IsDeleted\" = 0)";
        await using var command = Create(connection, insertGroup);
        command.Transaction = transaction;
        command.Parameters.Add("pId", OracleDbType.Raw, 16).Value = id;
        command.Parameters.Add("pCode", OracleDbType.NVarchar2, 50).Value = code;
        command.Parameters.Add("pName", OracleDbType.NVarchar2, 50).Value = name;
        command.Parameters.Add("pStamp", OracleDbType.NVarchar2, 40).Value = Guid.NewGuid().ToString("N");
        command.Parameters.Add("pUpperCode", OracleDbType.NVarchar2, 50).Value = code.ToUpperInvariant();
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (OracleException ex) when (ex.Number == 1)
        {
            var existing = await LockDictionaryGroupAsync(connection, transaction, code, ct);
            if (existing is not null) return existing;
            throw;
        }
        return await LockDictionaryGroupAsync(connection, transaction, code, ct)
            ?? throw new IOException($"无法创建标注字典分组 {code}");
    }

    private async Task<RemoteDictionary> ReadDictionaryAsync(OracleConnection connection, OracleTransaction? transaction, CancellationToken ct)
    {
        string sql = OracleAnnotationSql.SelectDictionary;
        await using var command = Create(connection, sql);
        if (transaction is not null) command.Transaction = transaction;
        command.Parameters.Add("pCategory", OracleDbType.NVarchar2, 50).Value = CategoryItemCode.ToUpperInvariant();
        command.Parameters.Add("pPlan", OracleDbType.NVarchar2, 50).Value = PlanItemCode.ToUpperInvariant();
        command.Parameters.Add("pRemark", OracleDbType.NVarchar2, 50).Value = RemarkItemCode.ToUpperInvariant();
        var categories = new List<RemoteDictionaryItem>();
        var plans = new List<RemoteDictionaryItem>();
        var remarks = new List<RemoteDictionaryItem>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string itemCode = (Text(reader, 0) ?? "").Trim();
                string name = Text(reader, 2) ?? Text(reader, 1) ?? "";
                if (name.Length == 0) continue;
                seenCodes.Add(itemCode);
                var item = new RemoteDictionaryItem(Text(reader, 1) is { Length: > 0 } value ? value : name, name);
                if (itemCode.Equals(CategoryItemCode, StringComparison.OrdinalIgnoreCase))
                    categories.Add(item with { Id = Convert.ToHexStringLower(reader.GetOracleBinary(3).Value) });
                else if (itemCode.Equals(PlanItemCode, StringComparison.OrdinalIgnoreCase)) plans.Add(item);
                else if (itemCode.Equals(RemarkItemCode, StringComparison.OrdinalIgnoreCase)) remarks.Add(item);
            }
        }
        Log.Information("标注字典加载完成；归类 {CategoryCount} 项，方案 {PlanCount} 项，命中的 ItemCode：{Codes}",
            categories.Count, plans.Count, string.Join(",", seenCodes));
        if (plans.Count == 0 && transaction is null) await LogPlanDiagnosticsAsync(connection, ct);
        return new RemoteDictionary(categories.ToArray(), plans.ToArray()) { Remarks = remarks.ToArray() };
    }

    /// <summary>解析目标表的实际 owner 和名称（引号内使用真实大小写）。</summary>
    private async Task<string> TableAsync(OracleConnection connection, CancellationToken ct)
    {
        if (resolvedTable is not null) return resolvedTable;
        const string sql = "SELECT owner, table_name FROM all_tables WHERE UPPER(table_name) = :pName";
        await using var command = Create(connection, sql);
        command.Parameters.Add("pName", OracleDbType.Varchar2, 128).Value = TargetTable;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var candidates = new List<(string Owner, string Name)>();
        while (await reader.ReadAsync(ct)) candidates.Add((reader.GetString(0), reader.GetString(1)));
        if (ChooseLogTable(candidates) is { } table)
        {
            resolvedTable = resolvedLogTableName = Qualify(table.Owner, table.Name);
            Log.Information("标注表已定位：{Table}（schema {Schema}）", resolvedTable, table.Owner);
        }
        else
        {
            Log.Warning("数据库里没有找到 {Table}，标注将无法写入", TargetTable);
            resolvedTable = Qualify("RCMDB", TargetTable);
        }
        return resolvedTable;
    }

    internal static (string Owner, string Name)? ChooseLogTable(IEnumerable<(string Owner, string Name)> candidates)
        => candidates.OrderByDescending(table => table.Owner.Equals("RCMDB", StringComparison.OrdinalIgnoreCase))
            .ThenBy(table => table.Owner, StringComparer.Ordinal).ThenBy(table => table.Name, StringComparer.Ordinal)
            .Select(table => ((string Owner, string Name)?)table).FirstOrDefault();

    internal static string Qualify(string owner, string name)
        => $"\"{owner.Replace("\"", "\"\"")}\".\"{name.Replace("\"", "\"\"")}\"";

    private async Task<string?> ResolveEquipmentTypeAsync(OracleConnection connection, string equipmentNo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipmentNo)) return null;
        if (equipmentTypes.TryGetValue(equipmentNo, out var cached)) return cached;
        const string sql = "SELECT t.\"Name\" FROM \"RCMDB\".\"RpaEquipmentInfos\" i " +
            "JOIN \"RCMDB\".\"RpaEquipmentTypes\" t ON t.\"Id\" = i.\"EquipmentTypeId\" " +
            "WHERE i.\"EquipmentNo\" = :pEqp";
        try
        {
            await using var command = Create(connection, sql);
            command.Parameters.Add("pEqp", OracleDbType.NVarchar2, 32).Value = equipmentNo;
            var value = await command.ExecuteScalarAsync(ct) as string;
            equipmentTypes[equipmentNo] = value;
            return value;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询设备类型失败；设备 {EquipmentNo}", equipmentNo);
            equipmentTypes[equipmentNo] = null;
            return null;
        }
    }

    private async Task LogPlanDiagnosticsAsync(OracleConnection connection, CancellationToken ct)
    {
        try
        {
            const string sql = "SELECT i.\"ItemCode\", COUNT(d.\"Id\") FROM \"RCMDB\".\"RpaDataItems\" i " +
                "LEFT JOIN \"RCMDB\".\"RpaDataItemDetails\" d ON d.\"ItemId\" = i.\"Id\" AND d.\"IsDeleted\" = 0 " +
                "WHERE UPPER(i.\"ItemCode\") LIKE 'EXCEPTION%' AND i.\"IsDeleted\" = 0 " +
                "GROUP BY i.\"ItemCode\"";
            await using var command = Create(connection, sql);
            var found = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add($"{Text(reader, 0)}({reader.GetInt64(1)})");
            Log.Warning("标注字典缺少处理方案，期望 ItemCode={Plan}；字典表里 Exception* 的条目：{Found}",
                PlanItemCode, found.Count == 0 ? "(无)" : string.Join(", ", found));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询标注字典诊断信息失败");
        }
    }

    private async Task<OracleConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private OracleCommand Create(OracleConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        command.CommandTimeout = commandTimeout;
        return command;
    }

    private static string? Text(OracleDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index).Trim();
}
