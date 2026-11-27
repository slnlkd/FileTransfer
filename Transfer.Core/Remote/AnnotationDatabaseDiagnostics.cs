namespace Transfer.Core.Remote;

public record AnnotationDatabaseTable(string Role, string DefaultName, string? ActualName, string Access,
    string? Details = null, string? ValidationState = null);
public record AnnotationSqlTemplate(string Operation, string Name, string Sql);
public record AnnotationDatabaseValidation(string Status, string Message, AnnotationDatabaseTable[] Tables);
public record AnnotationDatabaseInfo(string StorageMode, bool NgFilterEnabled, bool ResultLinkConfigured,
    int? CommandTimeoutSeconds, AnnotationDatabaseTable[] Tables, AnnotationSqlTemplate[] SqlTemplates)
{
    public AnnotationDatabaseValidation? Validation { get; init; }
}

/// <summary>单个服务实例的只读诊断。共享验证由服务生命周期取消。</summary>
public sealed class AnnotationDatabaseDiagnostics(Func<AnnotationDatabaseInfo> snapshot,
    Func<CancellationToken, Task<AnnotationDatabaseValidation>> verify)
{
    private readonly object gate = new();
    private Task<AnnotationDatabaseValidation>? pending;
    private AnnotationDatabaseValidation? latest;

    public async Task<AnnotationDatabaseInfo> ReadAsync(bool validate, CancellationToken serviceStop, CancellationToken caller)
    {
        AnnotationDatabaseValidation? result;
        if (validate)
        {
            Task<AnnotationDatabaseValidation> task;
            lock (gate) task = pending ??= VerifyAsync(serviceStop);
            result = await task.WaitAsync(caller);
        }
        else
        {
            lock (gate) result = latest;
        }
        return snapshot() with { Validation = result };
    }

    private async Task<AnnotationDatabaseValidation> VerifyAsync(CancellationToken serviceStop)
    {
        // 确保共享任务已经登记，再执行可能同步完成的验证。
        await Task.Yield();
        try
        {
            serviceStop.ThrowIfCancellationRequested();
            var result = await verify(serviceStop);
            serviceStop.ThrowIfCancellationRequested();
            lock (gate) latest = result;
            return result;
        }
        finally
        {
            lock (gate) pending = null;
        }
    }
}
