namespace RemoteFiles.Server;

internal static class OracleAnnotationSql
{
    private const string NoteColumn = "EXTEND_FIELD1";

    private const string ValidDictionaryItem = """
            FROM "RCMDB"."RpaDataItems" i
            JOIN "RCMDB"."RpaDataItemDetails" d ON d."ItemId" = i."Id"
            WHERE i."IsDeleted" = 0 AND d."IsDeleted" = 0
            """;

    private const string ResultFilter = """
        WHERE "EquipmentNo" = :equipment AND "AlignmentResult" = :result
          AND "AlignmentStartTime" >= :startDate AND "AlignmentStartTime" < :endDate
          AND TRIM("Parameter2") = :batch
        """;

    private const string ResultsTable = "\"RCMDB\".\"ProjectRpaAutoAlignmentResults\"";

    public const string SelectNgBatches = "SELECT DISTINCT TRUNC(\"AlignmentStartTime\"), \"Parameter2\" FROM \"RCMDB\".\"ProjectRpaAutoAlignmentResults\" " +
            "WHERE \"EquipmentNo\" = :equipmentNo AND \"AlignmentResult\" = :result " +
            "AND \"AlignmentStartTime\" >= :startUtc AND \"AlignmentStartTime\" < :endUtc AND \"Parameter2\" IS NOT NULL";

    public static string SelectHistory(string table, string? batch) => "SELECT \"JOB_BATCH_NUMBER\",\"FAIL_TYPE\",\"FAIL_HANDLE_SOLUTION\",\"ANALYSIS_USER\",\"ANALYSIS_DATETIME\"," +
            "\"FILE_DIRECTORY\",\"" + NoteColumn + "\",\"EQP_TYPE\" FROM " + table + " " +
            "WHERE \"EQP_ID\" = :pEqp AND TRUNC(\"AUTOLINE_DATE\") = :pDate" +
            (batch is null ? "" : " AND TRIM(\"JOB_BATCH_NUMBER\") = :pBatch") +
            " ORDER BY \"ANALYSIS_DATETIME\" DESC NULLS LAST, \"ID\" DESC";

    public static string SelectAnnotatedBatches(string table) => "SELECT DISTINCT TRUNC(\"AUTOLINE_DATE\"), \"JOB_BATCH_NUMBER\" FROM " + table +
            " WHERE \"EQP_ID\" = :pEqp AND \"AUTOLINE_DATE\" >= :pStart AND \"AUTOLINE_DATE\" < :pEnd AND \"JOB_BATCH_NUMBER\" IS NOT NULL";

    public static string InsertAnnotation(string table) => "INSERT INTO " + table + " " +
            "(\"ID\",\"EQP_ID\",\"EQP_TYPE\",\"AUTOLINE_DATE\",\"JOB_BATCH_NUMBER\",\"ANALYSIS_USER\",\"ANALYSIS_DATETIME\"," +
            "\"FAIL_TYPE\",\"FAIL_HANDLE_SOLUTION\",\"FILE_DIRECTORY\",\"" + NoteColumn + "\") " +
            "VALUES (:pId,:pEqp,:pEqpType,:pDate,:pBatch,:pUser,SYSDATE,:pType,:pPlan,:pDir,:pNote)";

    public static string DeleteImageAnnotations(string table) => "DELETE FROM " + table +
            " WHERE \"EQP_ID\" = :pEqp AND TRUNC(\"AUTOLINE_DATE\") = :pDate AND TRIM(\"JOB_BATCH_NUMBER\") = :pBatch" +
            " AND LOWER(RTRIM(REPLACE(\"FILE_DIRECTORY\", CHR(92), '/'), '/')) = LOWER(:pDir)";

    public const string SelectDictionary = "SELECT i.\"ItemCode\", d.\"ItemValue\", d.\"ItemName\", d.\"Id\" " +
            "FROM \"RCMDB\".\"RpaDataItems\" i JOIN \"RCMDB\".\"RpaDataItemDetails\" d ON d.\"ItemId\" = i.\"Id\" " +
            "WHERE (UPPER(TRIM(i.\"ItemCode\")) = :pCategory OR UPPER(TRIM(i.\"ItemCode\")) = :pPlan OR UPPER(TRIM(i.\"ItemCode\")) = :pRemark) " +
            "AND i.\"IsDeleted\" = 0 AND d.\"IsDeleted\" = 0 " +
            "ORDER BY d.\"SortCode\", d.\"ItemName\"";

    public static string LocateResult(string keyExpression, bool lockRow = false, bool byId = false)
        => $"SELECT {keyExpression} FROM {ResultsTable} {ResultFilter}"
           + (byId ? $" AND {keyExpression} = :id" : "")
           + (lockRow ? " FOR UPDATE" : "");

    public static string LocateLatestResult(string keyExpression)
        => $"""{LocateResult(keyExpression)} ORDER BY "AlignmentStartTime" DESC, {keyExpression} DESC FETCH FIRST 1 ROW ONLY""";

    public static string UpdateSelectedResult(string keyExpression) => $"""
            UPDATE {ResultsTable} SET "Parameter3" = :category
            {ResultFilter} AND {keyExpression} = :id
            AND EXISTS (SELECT 1 {ValidDictionaryItem}
                AND UPPER(TRIM(i."ItemCode")) = :categoryCode AND d."Id" = :categoryId
                AND UPPER(TRIM(NVL(d."ItemName", d."ItemValue"))) = UPPER(:categoryName))
            AND EXISTS (SELECT 1 {ValidDictionaryItem}
                AND UPPER(TRIM(i."ItemCode")) = :planCode
                AND UPPER(TRIM(NVL(d."ItemName", d."ItemValue"))) = UPPER(:planName))
            """;

    public const string SelectCategoryId = """
            SELECT d."Id" FROM "RCMDB"."RpaDataItems" i
            JOIN "RCMDB"."RpaDataItemDetails" d ON d."ItemId" = i."Id"
            WHERE UPPER(TRIM(i."ItemCode")) = :code AND i."IsDeleted" = 0 AND d."IsDeleted" = 0
              AND UPPER(TRIM(NVL(d."ItemName", d."ItemValue"))) = UPPER(:name)
            """;

    public static string SelectRemainingCategory(string table) => $"""
            SELECT "FAIL_TYPE" FROM {table}
            WHERE "EQP_ID" = :equipment AND TRUNC("AUTOLINE_DATE") = :batchDate AND TRIM("JOB_BATCH_NUMBER") = :batch
            ORDER BY "ANALYSIS_DATETIME" DESC NULLS LAST, "ID" DESC FETCH FIRST 1 ROW ONLY
            """;

    public static string UpdateResult(string keyExpression) => $"UPDATE {ResultsTable} SET \"Parameter3\" = :category WHERE {keyExpression} = :id";
}
