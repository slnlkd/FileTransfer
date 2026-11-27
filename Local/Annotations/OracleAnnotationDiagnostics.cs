using System.IO;
using Oracle.ManagedDataAccess.Client;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Server;

internal sealed class OracleAnnotationDiagnostics(NgFilterSettings? settings,
    OracleFailureAnalysisRepository? repository, bool ngEnabled)
{
    private const string Owner = "RCMDB";
    private const string Results = "ProjectRpaAutoAlignmentResults";
    private const string LogTable = OracleFailureAnalysisRepository.TargetTable;
    private const string DictionaryItems = "RpaDataItems";
    private const string DictionaryDetails = "RpaDataItemDetails";
    private const string Equipment = "RpaEquipmentInfos";
    private const string EquipmentTypes = "RpaEquipmentTypes";
    private static readonly string DictionaryGroups = string.Join(" / ", OracleFailureAnalysisRepository.CategoryItemCode,
        OracleFailureAnalysisRepository.PlanItemCode, OracleFailureAnalysisRepository.RemarkItemCode);

    private sealed record Table(string Role, string Name, string Access, string[] Required, string? Details = null);
    internal sealed record Column(string Name, string Type, int Bytes, int Characters, bool PrimaryKey, bool Nullable);
    private static readonly Table[] Tables =
    [
        new("NG 筛选与回写", Results, "查询结果，更新 Parameter3",
            ["EquipmentNo", "AlignmentResult", "AlignmentStartTime", "Parameter2", "Parameter3"]),
        new("标注日志", LogTable, "读取历史，新增和撤销标注",
            ["ID", "EQP_ID", "EQP_TYPE", "AUTOLINE_DATE", "JOB_BATCH_NUMBER", "ANALYSIS_USER", "ANALYSIS_DATETIME",
             "FAIL_TYPE", "FAIL_HANDLE_SOLUTION", "FILE_DIRECTORY", "EXTEND_FIELD1"]),
        new("字典分组", DictionaryItems, "读取和新增分组",
            ["Id", "ItemCode", "ItemName", "IsDeleted", "CreationTime", "ExtraProperties", "ConcurrencyStamp"], DictionaryGroups),
        new("字典明细", DictionaryDetails, "读取和新增归类、方案和备注",
            ["Id", "ItemId", "ItemName", "ItemValue", "IsDeleted", "SortCode", "CreationTime", "ExtraProperties", "ConcurrencyStamp"], DictionaryGroups),
        new("设备资料", Equipment, "读取设备编号与机型关联", ["EquipmentNo", "EquipmentTypeId"]),
        new("机型资料", EquipmentTypes, "读取机型名称", ["Id", "Name"])
    ];

    public AnnotationDatabaseInfo Snapshot()
    {
        bool oracle = repository is not null;
        var schema = repository?.ResolvedResultSchema;
        string? logTable = repository?.ResolvedLogTable;
        return new(oracle ? "Oracle" : "本地 JSON", ngEnabled, oracle && ngEnabled, settings?.AnnotationTimeout,
            Tables.Select(table => Describe(table, table.Name == LogTable ? logTable :
                table.Name == Results && schema is not null ? OracleFailureAnalysisRepository.Qualify(Owner, Results) : null,
                table.Name == Results ? schema?.Details : table.Details)).ToArray(),
            oracle && settings?.ExposeAnnotationSqlTemplates == true ? Templates(logTable, schema?.KeyExpression) : []);
    }

    private static AnnotationDatabaseTable Describe(Table table, string? actual = null, string? details = null,
        string status = "未验证") =>
        new(table.Role, $"{Owner}.{table.Name}", actual, table.Access, details, status);

    private static AnnotationSqlTemplate[] Templates(string? actualLogTable, string? resultKey)
    {
        string table = actualLogTable ?? "{标注日志表}";
        string key = resultKey ?? "{Result 主键表达式}";
        return
        [
            new("NG 筛选", "查找 NG 批次", OracleAnnotationSql.SelectNgBatches),
            new("结果定位", "查询最新 NG 的 Result ID", OracleAnnotationSql.LocateLatestResult(key)),
            new("字典读取", "读取三个标注字典分组", OracleAnnotationSql.SelectDictionary),
            new("提交", "按两个 ID 更新 Result", OracleAnnotationSql.UpdateSelectedResult(key)),
            new("提交", "写入标注日志", OracleAnnotationSql.InsertAnnotation(table)),
            new("撤销", "删除本图的标注", OracleAnnotationSql.DeleteImageAnnotations(table)),
            new("撤销", "读取批次剩余的最新归类", OracleAnnotationSql.SelectRemainingCategory(table)),
            new("撤销", "按 Result ID 锁定记录", OracleAnnotationSql.LocateResult(key, lockRow: true, byId: true)),
            new("撤销与兼容补查", "锁定 Result 行", OracleAnnotationSql.LocateResult(key, lockRow: true)),
            new("撤销与兼容补查", "按名称解析分类 ID", OracleAnnotationSql.SelectCategoryId),
            new("撤销与兼容补查", "同步或清空 Parameter3", OracleAnnotationSql.UpdateResult(key))
        ];
    }

    public async Task<AnnotationDatabaseValidation> VerifyAsync(CancellationToken ct)
    {
        if (settings is null || repository is null)
            return new("不适用", "当前使用本地 JSON 仓储。", []);
        bool connected = false;
        try
        {
            await using var connection = new OracleConnection(settings.ConnectionString);
            await connection.OpenAsync(ct);
            connected = true;
            var metadata = await ReadMetadataAsync(connection, settings.AnnotationTimeout, ct);
            return ValidateMetadata(metadata);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "标注数据库只读验证失败");
            ct.ThrowIfCancellationRequested();
            return new("失败", connected ? "已连接 Oracle，但无法读取数据库元数据。请查看服务端日志。"
                : "无法连接 Oracle。请查看服务端日志。", []);
        }
    }

    internal static AnnotationDatabaseValidation ValidateMetadata(Dictionary<(string Owner, string Name), List<Column>> metadata)
    {
        var checks = Tables.Select(table => ValidateTable(table, metadata)).ToArray();
        bool valid = checks.All(table => table.ValidationState == "通过");
        return new(valid ? "通过" : "失败", valid ? "Oracle 连接和所需数据库结构已验证。"
            : "已连接 Oracle，部分数据库结构与标注要求不符。", checks);
    }

    private static AnnotationDatabaseTable ValidateTable(Table table, Dictionary<(string Owner, string Name), List<Column>> metadata)
    {
        var identity = table.Name == LogTable
            ? OracleFailureAnalysisRepository.ChooseLogTable(metadata.Keys.Where(key => key.Name.Equals(LogTable, StringComparison.OrdinalIgnoreCase)))
            : (Owner, table.Name);
        if (identity is null || !metadata.TryGetValue(identity.Value, out var columns))
            return Describe(table, details: "未找到可访问的表。", status: "失败");

        string actual = OracleFailureAnalysisRepository.Qualify(identity.Value.Owner, identity.Value.Name);
        var missing = table.Required.Except(columns.Select(column => column.Name), StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) return Describe(table, actual, "缺少列：" + string.Join("、", missing), "失败");
        if (table.Name != Results) return Describe(table, actual, table.Details, "通过");

        var parameter = columns.Single(column => column.Name == "Parameter3");
        var dictionaryId = metadata.GetValueOrDefault((Owner, DictionaryDetails))?.SingleOrDefault(column => column.Name == "Id");
        var keys = columns.Where(column => column.PrimaryKey).Select(column => (column.Name, column.Type)).ToArray();
        string detail = $"主键 {string.Join("、", keys.Select(key => key.Name))}，Parameter3 {parameter.Type}" +
            $"({(parameter.Type == "RAW" ? parameter.Bytes : parameter.Characters)})";
        try
        {
            var schema = OracleFailureAnalysisRepository.InterpretResultSchema(keys, dictionaryId?.Type == "RAW" ? dictionaryId.Bytes : 0,
                parameter.Type, parameter.Bytes, parameter.Characters);
            if (!parameter.Nullable) return Describe(table, actual, schema.Details + "，该列必须允许撤销时清空。", "失败");
            return Describe(table, actual, schema.Details, "通过");
        }
        catch (IOException ex) { return Describe(table, actual, detail + "。 " + ex.Message, "失败"); }
    }

    private static async Task<Dictionary<(string Owner, string Name), List<Column>>> ReadMetadataAsync(
        OracleConnection connection, int timeout, CancellationToken ct)
    {
        const string sql = """
            SELECT c.owner, c.table_name, c.column_name, c.data_type, c.data_length, c.char_length, c.nullable, k.position
            FROM all_tab_columns c
            JOIN all_tables t ON t.owner = c.owner AND t.table_name = c.table_name
            LEFT JOIN (
                SELECT a.owner, a.table_name, b.column_name, b.position
                FROM all_constraints a
                JOIN all_cons_columns b ON b.owner = a.owner AND b.constraint_name = a.constraint_name
                WHERE a.constraint_type = 'P' AND a.status = 'ENABLED'
            ) k ON k.owner = c.owner AND k.table_name = c.table_name AND k.column_name = c.column_name
            WHERE UPPER(c.table_name) = :logTable OR
                (c.owner = :owner AND c.table_name IN (:results, :groups, :details, :equipment, :types))
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        command.BindByName = true;
        command.Parameters.Add("logTable", OracleDbType.Varchar2).Value = LogTable;
        command.Parameters.Add("owner", OracleDbType.Varchar2).Value = Owner;
        command.Parameters.Add("results", OracleDbType.Varchar2).Value = Results;
        command.Parameters.Add("groups", OracleDbType.Varchar2).Value = DictionaryItems;
        command.Parameters.Add("details", OracleDbType.Varchar2).Value = DictionaryDetails;
        command.Parameters.Add("equipment", OracleDbType.Varchar2).Value = Equipment;
        command.Parameters.Add("types", OracleDbType.Varchar2).Value = EquipmentTypes;
        var metadata = new Dictionary<(string Owner, string Name), List<Column>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!metadata.TryGetValue(key, out var columns)) metadata[key] = columns = [];
            columns.Add(new(reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5), !reader.IsDBNull(7), reader.GetString(6) == "Y"));
        }
        return metadata;
    }
}
