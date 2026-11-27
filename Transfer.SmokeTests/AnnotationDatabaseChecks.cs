using Transfer.Core.Remote;

internal static class AnnotationDatabaseChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var table = new AnnotationDatabaseTable("NG 筛选与回写", "RCMDB.ProjectRpaAutoAlignmentResults", null, "查询和更新");
        var snapshot = new AnnotationDatabaseInfo("Oracle", true, true, 10, [table], []);
        var verified = new AnnotationDatabaseValidation("通过", "结构已验证", [table with { ActualName = table.DefaultName }]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledByStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        bool expose = false;
        var diagnostics = new AnnotationDatabaseDiagnostics(
            () => expose ? snapshot with { SqlTemplates = [new("字典", "读取", "SELECT :category FROM DUAL")] } : snapshot,
            async ct =>
            {
                Interlocked.Increment(ref probes);
                entered.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { canceledByStop.TrySetResult(); throw; }
                return verified;
            });
        await using var service = new RemoteFileService(root) { AnnotationDiagnostics = diagnostics };
        service.Start(0);
        var client = new RemoteFileClient("127.0.0.1", service.Port);
        var info = await client.GetAnnotationDatabaseInfoAsync(false);
        check(info is { StorageMode: "Oracle", CommandTimeoutSeconds: 10, Validation: null, SqlTemplates.Length: 0 }
            && info.Tables.Single().ActualName is null && probes == 0, "诊断摘要不探测 Oracle，默认不返回 SQL");
        expose = true;
        check((await client.GetAnnotationDatabaseInfoAsync(false))?.SqlTemplates.Single().Sql == "SELECT :category FROM DUAL"
            && probes == 0, "开放的参数化 SQL 随摘要返回且不触发验证");

        // 直接调用第二个等待者，保证第一个网络请求正在共享验证中。
        var network = client.GetAnnotationDatabaseInfoAsync(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var canceled = diagnostics.ReadAsync(true, CancellationToken.None, caller.Token);
        var shared = diagnostics.ReadAsync(true, CancellationToken.None, CancellationToken.None);
        caller.Cancel();
        try { await canceled; throw new Exception("客户端应可取消自己的等待"); }
        catch (OperationCanceledException) { }
        release.SetResult();
        check((await network)?.Validation?.Tables.Single().ActualName == table.DefaultName
            && (await shared).Validation == verified && probes == 1, "并发验证合并，取消一个等待者不影响其他请求");
        check((await client.GetAnnotationDatabaseInfoAsync(false))?.Validation?.Status == "通过" && probes == 1,
            "摘要复用服务实例的最近验证结果");

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = client.GetAnnotationDatabaseInfoAsync(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync();
        await canceledByStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await interrupted; }
        catch (IOException) { }
        check(probes == 2, "服务停止取消共享 Oracle 验证");
    }
}
