extern alias RemoteClient;
extern alias RemoteServer;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Transfer.Core.Remote;
using AnnotationDatabaseWindow = RemoteClient::RemoteFiles.Client.AnnotationDatabaseWindow;
using OracleDiagnostics = RemoteServer::RemoteFiles.Server.OracleAnnotationDiagnostics;
using OracleSettings = RemoteServer::RemoteFiles.Server.NgFilterSettings;
using OracleRepository = RemoteServer::RemoteFiles.Server.OracleFailureAnalysisRepository;
using OracleSql = RemoteServer::RemoteFiles.Server.OracleAnnotationSql;

internal static partial class RemoteWindowChecks
{
    private static async Task CheckAnnotationDatabaseWindowAsync(string root)
    {
        await CheckOracleDiagnosticRulesAsync();
        int summaries = 0, verifies = 0;
        var table = new AnnotationDatabaseTable("标注日志", "RCMDB.AUTOLINEFAILUREANALYSISLOG", null, "新增和撤销");
        var longLiteral = "'" + new string('A', 240) + "'";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new RemoteFileService(root)
        {
            AnnotationDiagnostics = new(() =>
            {
                Interlocked.Increment(ref summaries);
                return new("Oracle", true, true, 10, [table],
                    [new("提交", "示例模板", "SELECT :id FROM DUAL"),
                     new("撤销", "另一个模板", $"SELECT {longLiteral} FROM DUAL WHERE X = :batch")]);
            }, async ct =>
            {
                Interlocked.Increment(ref verifies);
                await release.Task.WaitAsync(ct);
                return new("通过", "已验证", [table with { ActualName = table.DefaultName, ValidationState = "通过" }]);
            })
        };
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        var window = new AnnotationDatabaseWindow(endpoint);
        Prepare(window);
        window.Show();
        try
        {
            var verify = FindButtonByContent(window, "验证数据库") ?? throw new Exception("缺少数据库验证按钮");
            await WaitAsync(() => verify.IsEnabled);
            if (summaries != 1 || verifies != 0) throw new Exception("打开诊断窗口应只读取一次摘要");
            await WaitAsync(() => FindTextBlock(window, "未验证") is not null);
            var unverified = FindTextBlock(window, "未验证")!;
            var defaultName = FindTextBlock(window, table.DefaultName)!;
            var actualName = FindTextBlock(window, "未解析")!;
            if (!Equals(unverified.Foreground, window.FindResource("WarningBrush"))
                || !Equals(defaultName.FontFamily, window.FindResource("UiFontFamily"))
                || !Equals(actualName.FontFamily, defaultName.FontFamily))
                throw new Exception("表名应使用统一界面字体，未验证状态应使用警示色");
            var tabs = FindDescendant<TabControl>(window) ?? throw new Exception("缺少诊断选项卡");
            window.UpdateLayout();
            var activeTab = (TabItem)tabs.SelectedItem;
            var tabTop = activeTab.TranslatePoint(new Point(), window).Y;
            var buttonTop = verify.TranslatePoint(new Point(), window).Y;
            if (tabTop >= buttonTop + verify.ActualHeight || buttonTop >= tabTop + activeTab.ActualHeight)
                throw new Exception("验证按钮和选项卡应在同一行");
            tabs.SelectedIndex = 1;
            var picker = (ComboBox)window.FindName("TemplatePicker");
            var sql = (RichTextBox)window.FindName("SqlText");
            await WaitAsync(() => SqlPreviewText(sql).Contains(":id", StringComparison.Ordinal));
            var rendered = (Paragraph)sql.Document.Blocks.FirstBlock;
            if (!sql.IsReadOnly || sql.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                || !rendered.Inlines.OfType<LineBreak>().Any()
                || !rendered.Inlines.OfType<Run>().Any(run => run.Text == "SELECT" && run.FontWeight == FontWeights.SemiBold))
                throw new Exception("SQL 模板应只读、分行高亮并禁用横向滚动");
            window.UpdateLayout();
            if (!picker.IsVisible || picker.Template.FindName("PART_Popup", picker) is not Popup popup)
                throw new Exception($"SQL 选择器未完成布局：Visible={picker.IsVisible}，Height={picker.ActualHeight}");
            picker.IsDropDownOpen = true;
            await WaitAsync(() => popup.IsOpen && picker.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem);
            picker.SelectedIndex = 1;
            picker.IsDropDownOpen = false;
            await WaitAsync(() => !popup.IsOpen && SqlPreviewText(sql).Contains(":batch", StringComparison.Ordinal));
            window.UpdateLayout();
            var literal = ((Paragraph)sql.Document.Blocks.FirstBlock).Inlines.OfType<Run>().Single(run => run.Text == longLiteral);
            if (literal.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top >=
                literal.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Top)
                throw new Exception("超过窗口宽度的 SQL 内容应自动折行");
            if (summaries != 1 || verifies != 0) throw new Exception("查看 SQL 模板不应重新请求服务端");
            verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => verifies == 1 && !verify.IsEnabled);
            window.Close();
            release.SetResult();
            await WaitAsync(() => summaries == 2);
            if (window.IsVisible) throw new Exception("验证的晚到响应不应重新打开诊断窗口");
            var verifiedWindow = new AnnotationDatabaseWindow(endpoint);
            Prepare(verifiedWindow);
            verifiedWindow.Show();
            try
            {
                await WaitAsync(() => FindTextBlock(verifiedWindow, "已验证") is not null);
                if (!Equals(FindTextBlock(verifiedWindow, "已验证")!.Foreground, verifiedWindow.FindResource("SuccessBrush")))
                    throw new Exception("已验证状态应使用成功色");
            }
            finally { await CloseAsync(verifiedWindow); }
        }
        finally
        {
            release.TrySetResult();
            if (window.IsVisible) await CloseAsync(window);
        }
        Console.WriteLine("通过：标注数据库窗口单次摘要、模板本地切换、手动验证和关闭后忽略结果");
    }

    private static string SqlPreviewText(RichTextBox preview) =>
        new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text;

    private static async Task CheckOracleDiagnosticRulesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var json = new OracleDiagnostics(null, null, false);
        if (json.Snapshot().StorageMode != "本地 JSON" || (await json.VerifyAsync(timeout.Token)).Status != "不适用")
            throw new Exception("JSON 模式应跳过 Oracle 探测");
        var settings = new OracleSettings("User Id=example;Password=private-test-value");
        var repository = new OracleRepository(settings);
        if (new OracleDiagnostics(settings, repository, true).Snapshot().SqlTemplates.Length != 0)
            throw new Exception("SQL 默认不应开放");
        var sql = new OracleDiagnostics(settings with { ExposeAnnotationSqlTemplates = true }, repository, true).Snapshot();
        if (!sql.SqlTemplates.Any(item => item.Sql == OracleSql.UpdateSelectedResult("{Result 主键表达式}"))
            || System.Text.Json.JsonSerializer.Serialize(sql).Contains("private-test-value", StringComparison.Ordinal))
            throw new Exception("诊断应返回业务模板，摘要不得包含连接串");
        const string keyExpression = """RAWTOHEX("Id")""";
        const string displayedKey = "{Result 主键表达式}";
        if (!sql.SqlTemplates.Any(item => item.Operation == "结果定位" && item.Sql.EndsWith(
                $"""ORDER BY "AlignmentStartTime" DESC, {displayedKey} DESC FETCH FIRST 1 ROW ONLY""", StringComparison.Ordinal)))
            throw new Exception("标注窗口应按对线开始时间和主键确定最新一条 NG 记录");
        string update = OracleSql.UpdateSelectedResult(keyExpression);
        string lockResult = OracleSql.LocateResult(keyExpression, lockRow: true, byId: true);
        if (!update.Contains($"{keyExpression} = :id", StringComparison.Ordinal)
            || update.Contains($"{keyExpression} <> :id", StringComparison.Ordinal)
            || !lockResult.EndsWith($"{keyExpression} = :id FOR UPDATE", StringComparison.Ordinal))
            throw new Exception("有明确 Result ID 时应仅按该 ID 和批次条件定位目标");

        var metadata = new Dictionary<(string, string), List<OracleDiagnostics.Column>>();
        var absent = OracleDiagnostics.ValidateMetadata(metadata);
        if (absent.Status != "失败" || absent.Tables.Any(row => row.ActualName is not null))
            throw new Exception("缺表应标记失败，不应冒充实际表");
        var key = ("RCMDB", "ProjectRpaAutoAlignmentResults");
        metadata[key] = [new("Id", "RAW", 16, 0, true, false)];
        if (OracleDiagnostics.ValidateMetadata(metadata).Tables[0].Details?.Contains("缺少列", StringComparison.Ordinal) != true)
            throw new Exception("缺少必要列时应明确指出");
        metadata[key].AddRange(
        [
            new("EquipmentNo", "NVARCHAR2", 100, 50, false, true),
            new("AlignmentResult", "NVARCHAR2", 100, 50, false, true),
            new("AlignmentStartTime", "TIMESTAMP", 11, 0, false, true),
            new("Parameter2", "NVARCHAR2", 100, 50, false, true),
            new("Parameter3", "NVARCHAR2", 4000, 2000, false, true)
        ]);
        metadata[("RCMDB", "RpaDataItemDetails")] = [new("Id", "RAW", 16, 0, true, false)];
        if (OracleDiagnostics.ValidateMetadata(metadata).Tables[0] is not { ValidationState: "通过", Details: var detail }
            || detail?.Contains("NVARCHAR2(2000)", StringComparison.Ordinal) != true)
            throw new Exception("应识别 Result 的单列主键和列型");
        metadata[key].Add(new("OtherKey", "NUMBER", 22, 0, true, false));
        if (OracleDiagnostics.ValidateMetadata(metadata).Tables[0].ValidationState != "失败")
            throw new Exception("复合主键应标记为不兼容");
    }
}
