using System.IO;
using Oracle.ManagedDataAccess.Client;
using Transfer.Core.Remote;

namespace RemoteFiles.Server;

internal sealed partial class OracleFailureAnalysisRepository
{
    internal sealed record ResultSchema(string KeyExpression, OracleDbType ParameterType, string Details);
    internal ResultSchema? ResolvedResultSchema => resultSchema;
    private sealed record ResultTarget(ResultSchema Schema, string Id);
    private ResultSchema? resultSchema;

    public async Task<string?> FindResultIdAsync(string equipmentNo, DateTime date, string batch, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var schema = resultSchema ??= await ReadResultSchemaAsync(connection, null, ct);
        await using var command = Create(connection, OracleAnnotationSql.LocateLatestResult(schema.KeyExpression));
        BindResultFilter(command, equipmentNo, date, batch);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? reader.GetString(0) : null;
    }

    private async Task<ResultTarget?> FindWriteTargetAsync(OracleConnection connection, OracleTransaction transaction,
        string equipment, DateTime date, string batch, bool required, string? expectedId, CancellationToken ct)
    {
        var target = await FindResultAsync(connection, transaction, equipment, date, batch, ct, expectedId);
        if (target is not null) return target;
        if (expectedId is not null)
            throw new IOException("所选 NG 结果已变化，请重新打开标注窗口。");
        if (required)
            throw new IOException("没有找到对应的 NG 结果，请刷新目录后重试。");
        return null;
    }

    private async Task<ResultTarget?> FindResultAsync(OracleConnection connection, OracleTransaction transaction,
        string equipment, DateTime date, string batch, CancellationToken ct, string? expectedId)
    {
        var schema = resultSchema ??= await ReadResultSchemaAsync(connection, transaction, ct);
        string sql = OracleAnnotationSql.LocateResult(schema.KeyExpression, lockRow: true, byId: expectedId is not null);
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        BindResultFilter(command, equipment, date, batch);
        if (expectedId is not null) command.Parameters.Add("id", OracleDbType.NVarchar2).Value = expectedId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        string id = reader.GetString(0);
        if (expectedId is null && await reader.ReadAsync(ct))
            throw new IOException("当前标注操作缺少 Result ID，且该批次有多条 NG 结果。请从 NG 总览重新打开标注窗口。");
        return new(schema, id);
    }

    private static void BindResultFilter(OracleCommand command, string equipment, DateTime date, string batch)
    {
        command.Parameters.Add("equipment", OracleDbType.NVarchar2, 50).Value = equipment;
        command.Parameters.Add("result", OracleDbType.NVarchar2, 50).Value = "N";
        command.Parameters.Add("startDate", OracleDbType.TimeStamp).Value = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        command.Parameters.Add("endDate", OracleDbType.TimeStamp).Value = DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Unspecified);
        command.Parameters.Add("batch", OracleDbType.NVarchar2).Value = BatchNames.Normalize(batch);
    }

    private async Task UpdateSelectedResultAsync(OracleConnection connection, OracleTransaction transaction,
        RemoteAnnotation annotation, string resultId, string dictionaryId, CancellationToken ct)
    {
        if (dictionaryId.Length != 32 || !dictionaryId.All(Uri.IsHexDigit))
            throw new IOException("异常归类字典 Id 格式无效，请重新打开标注窗口。");
        byte[] categoryId = Convert.FromHexString(dictionaryId);
        var schema = resultSchema ??= await ReadResultSchemaAsync(connection, transaction, ct);
        string sql = OracleAnnotationSql.UpdateSelectedResult(schema.KeyExpression);
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        BindResultFilter(command, annotation.EquipmentNo!, annotation.AutoLineDate!.Value, annotation.BatchNumber!);
        command.Parameters.Add("id", OracleDbType.NVarchar2).Value = resultId;
        command.Parameters.Add("category", schema.ParameterType, 32).Value =
            schema.ParameterType == OracleDbType.Raw ? categoryId : Convert.ToHexStringLower(categoryId);
        command.Parameters.Add("categoryId", OracleDbType.Raw, 16).Value = categoryId;
        command.Parameters.Add("categoryCode", OracleDbType.NVarchar2, 50).Value = CategoryItemCode.ToUpperInvariant();
        command.Parameters.Add("categoryName", OracleDbType.NVarchar2, 255).Value = annotation.CategoryName!;
        command.Parameters.Add("planCode", OracleDbType.NVarchar2, 50).Value = PlanItemCode.ToUpperInvariant();
        command.Parameters.Add("planName", OracleDbType.NVarchar2, 255).Value = annotation.PlanName!;
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new IOException("所选 NG 结果或标注字典已变化，请重新打开标注窗口。");
    }

    private async Task<ResultSchema> ReadResultSchemaAsync(OracleConnection connection, OracleTransaction? transaction, CancellationToken ct)
    {
        const string sql = """
            SELECT c.table_name, c.column_name, c.data_type, c.data_length, c.char_length, k.position
            FROM all_tab_columns c
            LEFT JOIN (
                SELECT a.table_name, b.column_name, b.position
                FROM all_constraints a
                JOIN all_cons_columns b ON b.owner = a.owner AND b.constraint_name = a.constraint_name
                WHERE a.owner = 'RCMDB' AND a.constraint_type = 'P' AND a.status = 'ENABLED'
            ) k ON k.table_name = c.table_name AND k.column_name = c.column_name
            WHERE c.owner = 'RCMDB' AND (
                (c.table_name = 'ProjectRpaAutoAlignmentResults' AND (c.column_name = 'Parameter3' OR k.position IS NOT NULL))
                OR (c.table_name = 'RpaDataItemDetails' AND c.column_name = 'Id')
            )
            """;
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        var keys = new List<(string Name, string Type)>();
        int dictionaryBytes = 0;
        string? parameterType = null;
        int parameterBytes = 0, parameterChars = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string table = reader.GetString(0), column = reader.GetString(1), type = reader.GetString(2);
            if (table == "RpaDataItemDetails" && type == "RAW") dictionaryBytes = reader.GetInt32(3);
            if (table != "ProjectRpaAutoAlignmentResults") continue;
            if (!reader.IsDBNull(5)) keys.Add((column, type));
            if (column != "Parameter3") continue;
            parameterType = type;
            parameterBytes = reader.GetInt32(3);
            parameterChars = reader.GetInt32(4);
        }
        return InterpretResultSchema(keys, dictionaryBytes, parameterType, parameterBytes, parameterChars);
    }

    internal static ResultSchema InterpretResultSchema(IReadOnlyList<(string Name, string Type)> keys, int dictionaryBytes,
        string? parameterType, int parameterBytes, int parameterChars)
    {
        if (keys.Count != 1 || dictionaryBytes != 16)
            throw new IOException("NG 联动要求对线结果表使用单列主键，且异常归类字典 Id 为 RAW(16)。");
        var key = keys[0];
        string identifier = "\"" + key.Name.Replace("\"", "\"\"") + "\"";
        string expression = key.Type switch
        {
            "RAW" => $"RAWTOHEX({identifier})",
            "NUMBER" => $"TO_CHAR({identifier}, 'TM9', 'NLS_NUMERIC_CHARACTERS = ''.,''')",
            "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" => identifier,
            _ => throw new IOException("NG 结果表主键列类型不受支持。")
        };
        var bindType = parameterType switch
        {
            "RAW" when parameterBytes >= dictionaryBytes => OracleDbType.Raw,
            "VARCHAR2" when parameterChars >= 2 * dictionaryBytes => OracleDbType.Varchar2,
            "NVARCHAR2" when parameterChars >= 2 * dictionaryBytes => OracleDbType.NVarchar2,
            "CHAR" when parameterChars >= 2 * dictionaryBytes => OracleDbType.Char,
            "NCHAR" when parameterChars >= 2 * dictionaryBytes => OracleDbType.NChar,
            _ => throw new IOException("Parameter3 列类型或长度不支持保存异常归类字典 Id。")
        };
        int length = bindType == OracleDbType.Raw ? parameterBytes : parameterChars;
        return new(expression, bindType, $"主键 {key.Name}（{key.Type}），Parameter3 {parameterType}({length})");
    }

    private async Task UpdateResultAsync(OracleConnection connection, OracleTransaction transaction, ResultTarget target,
        string? category, CancellationToken ct)
    {
        object value = DBNull.Value;
        if (category is not null)
        {
            byte[] id = await FindCategoryIdAsync(connection, transaction, category, ct);
            value = target.Schema.ParameterType == OracleDbType.Raw ? id : Convert.ToHexStringLower(id);
        }
        await using var command = Create(connection, OracleAnnotationSql.UpdateResult(target.Schema.KeyExpression));
        command.Transaction = transaction;
        command.Parameters.Add("category", target.Schema.ParameterType, 32).Value = value;
        command.Parameters.Add("id", OracleDbType.NVarchar2).Value = target.Id;
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new IOException("更新 Parameter3 未命中唯一的 NG 结果。");
    }

    private async Task<byte[]> FindCategoryIdAsync(OracleConnection connection, OracleTransaction transaction,
        string category, CancellationToken ct)
    {
        string sql = OracleAnnotationSql.SelectCategoryId;
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        command.Parameters.Add("code", OracleDbType.NVarchar2, 50).Value = CategoryItemCode.ToUpperInvariant();
        command.Parameters.Add("name", OracleDbType.NVarchar2, 255).Value = category.Trim();
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new IOException("无法找到有效的异常归类字典 Id：" + category);
        byte[] id = reader.GetOracleBinary(0).Value;
        if (id.Length != 16 || await reader.ReadAsync(ct))
            throw new IOException("异常归类无法解析为唯一有效的字典 Id：" + category);
        return id;
    }

    private async Task<string?> FindLatestCategoryAsync(OracleConnection connection, OracleTransaction transaction, string table,
        string equipment, DateTime date, string batch, CancellationToken ct)
    {
        string sql = OracleAnnotationSql.SelectRemainingCategory(table);
        await using var command = Create(connection, sql);
        command.Transaction = transaction;
        command.Parameters.Add("equipment", OracleDbType.NVarchar2, 32).Value = equipment;
        command.Parameters.Add("batchDate", OracleDbType.Date).Value = date.Date;
        command.Parameters.Add("batch", OracleDbType.NVarchar2, 32).Value = BatchNames.Normalize(batch);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        string? category = Text(reader, 0);
        return string.IsNullOrEmpty(category) ? throw new IOException("剩余标注缺少异常归类，无法同步 Parameter3。") : category;
    }
}
