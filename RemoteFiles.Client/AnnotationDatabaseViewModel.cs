using System.ComponentModel;
using Transfer.Core.Remote;

namespace RemoteFiles.Client;

internal sealed class AnnotationDatabaseViewModel(
    Func<bool, CancellationToken, Task<AnnotationDatabaseInfo?>> fetch) : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private AnnotationDatabaseInfo? info;
    private bool disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsBusy { get; private set; }
    public bool CanVerify => !IsBusy && info?.StorageMode == "Oracle";
    public string Status { get; private set; } = "";
    public string SqlStatus => info is { StorageMode: "Oracle", SqlTemplates.Length: 0 }
        ? "服务端未开放 SQL 模板。" : "";
    public IReadOnlyList<AnnotationDatabaseTable> Tables => info switch
    {
        { Validation.Tables.Length: > 0 } => info.Validation.Tables,
        not null => info.Tables,
        _ => []
    };
    public IReadOnlyList<AnnotationSqlTemplate> SqlTemplates => info?.SqlTemplates ?? [];
    public bool HasSqlTemplates => SqlTemplates.Count > 0;

    public async Task LoadAsync(bool verify)
    {
        if (disposed || IsBusy || (verify && !CanVerify)) return;
        IsBusy = true;
        Status = verify ? "正在验证数据库…" : "正在读取数据库详情…";
        Notify();
        var token = lifetime.Token;
        try
        {
            var response = await fetch(verify, token);
            if (disposed) return;
            info = response;
            Status = response switch
            {
                null => "当前服务端不提供数据库详情。",
                { Validation: { Status: "失败" } validation } => validation.Message,
                { StorageMode: "Oracle" } => "",
                _ => "本地 JSON 模式无需验证数据库。"
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed) Status = verify ? "未能获取验证结果，请检查连接后重试。" : "无法读取诊断摘要，请检查连接后重试。";
        }
        finally
        {
            if (!disposed) { IsBusy = false; Notify(); }
        }
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
