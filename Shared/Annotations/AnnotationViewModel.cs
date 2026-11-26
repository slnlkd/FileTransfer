using System.ComponentModel;
using System.Runtime.CompilerServices;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

public enum AnnotationWriteKind { Submit, Revoke }

/// <summary>每个标注窗口独立的表单与异步状态。所有调用由宿主 UI 上下文串行发起。</summary>
public sealed class AnnotationViewModel(
    IAnnotationClient client, AnnotationContext context, string operatorName, Action<string>? saveOperator = null)
    : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly string defaultOperator = operatorName;
    private CancellationTokenSource selection = new();
    private bool disposed, isDirty, isWriting, isAddingDictionary;
    private int imageVersion, dictionaryVersion;
    private string category = "", plan = "", note = "", operatorText = operatorName, error = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AnnotationWriteKind>? WriteSucceeded;
    public string ImageName { get; private set; } = "";
    public IReadOnlyList<RemoteAnnotation> History { get; private set; } = [];
    public RemoteDictionary Dictionary { get; private set; } = new([], []);
    public string Category { get => category; set => ChangeDraft(ref category, value); }
    public string Plan { get => plan; set => ChangeDraft(ref plan, value); }
    public string Note { get => note; set => ChangeDraft(ref note, value); }
    public string Operator { get => operatorText; set => ChangeDraft(ref operatorText, value); }
    public string Error { get => error; private set { error = value; Changed(); } }
    public string? ErrorField { get; private set; }
    public bool IsDirty { get => isDirty; private set { isDirty = value; Changed(); } }
    public bool IsWriting { get => isWriting; private set { isWriting = value; Changed(); Changed(nameof(IsBusy)); } }
    public bool IsAddingDictionary { get => isAddingDictionary; private set { isAddingDictionary = value; Changed(); Changed(nameof(IsBusy)); } }
    public bool IsBusy => IsWriting || IsAddingDictionary;
    public AnnotationWriteKind? PendingWrite { get; private set; }

    public void SelectImage(string imageName)
    {
        if (disposed) return;
        selection.Cancel(); selection.Dispose(); selection = new();
        imageVersion++;
        ImageName = imageName;
        History = context.GetImageHistory(imageName);
        category = plan = note = "";
        operatorText = defaultOperator;
        ClearError();
        IsDirty = false;
        foreach (string property in new[] { nameof(ImageName), nameof(History), nameof(Category), nameof(Plan), nameof(Note), nameof(Operator) })
            Changed(property);
    }

    public void DiscardDraft() { IsDirty = false; }
    public void ClearError() { ErrorField = null; Error = ""; }
    private void ChangeDraft(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (disposed || field == value) return;
        field = value;
        IsDirty = true;
        ClearError();
        Changed(property);
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private bool Invalid(string message, string? field = null)
    {
        ErrorField = field; Error = message; return false;
    }

    public bool AppendRemark(string name)
    {
        if (disposed || IsBusy || string.IsNullOrWhiteSpace(name)) return false;
        if (Note.Split(['；', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(text => string.Equals(text.Trim(), name, StringComparison.Ordinal))) return false;
        string existing = Note.TrimEnd();
        string updated = existing.Length == 0 ? name : existing + "；" + name;
        if (updated.Length > AnnotationStore.NoteLimit) return Invalid($"备注不能超过 {AnnotationStore.NoteLimit} 个字符", nameof(Note));
        Note = updated;
        return true;
    }

    public async Task LoadDictionaryAsync()
    {
        if (disposed) return;
        int version = ++dictionaryVersion;
        try
        {
            var words = await client.GetDictionaryAsync(lifetime.Token);
            if (disposed || version != dictionaryVersion) return;
            Dictionary = words; Changed(nameof(Dictionary));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!disposed && version == dictionaryVersion) Invalid("读取标注字典失败：" + ex.Message);
        }
    }

    public async Task<bool> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string text)
    {
        if (disposed || IsBusy) return false;
        string name = text.Trim();
        if (name.Length == 0) return Invalid("请输入名称");
        if (name.Length > AnnotationStore.NameLimit) return Invalid($"字典项名称不能超过 {AnnotationStore.NameLimit} 个字符");
        if (name.Any(char.IsControl)) return Invalid("字典项名称不能包含控制字符");
        int selectedVersion = imageVersion;
        int version = ++dictionaryVersion;
        IsAddingDictionary = true;
        try
        {
            var words = await client.AddDictionaryItemAsync(kind, name, lifetime.Token);
            if (disposed) return false;
            if (version == dictionaryVersion) { Dictionary = words; Changed(nameof(Dictionary)); }
            if (selectedVersion == imageVersion)
            {
                if (kind == AnnotationDictionaryKind.Category) Category = name;
                else if (kind == AnnotationDictionaryKind.Plan) Plan = name;
            }
            ClearError();
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) { return disposed ? false : Invalid(ex.Message); }
        finally { IsAddingDictionary = false; }
    }

    public Task<bool> SubmitAsync()
    {
        if (disposed || IsBusy) return Task.FromResult(false);
        ClearError();
        if (ImageName.Length == 0) return Task.FromResult(Invalid("请选择图片"));
        if (string.IsNullOrWhiteSpace(Category)) return Task.FromResult(Invalid("请选择异常归类", nameof(Category)));
        if (string.IsNullOrWhiteSpace(Plan)) return Task.FromResult(Invalid("请选择异常处理方案", nameof(Plan)));
        if (string.IsNullOrWhiteSpace(Operator)) return Task.FromResult(Invalid("请填写操作人", nameof(Operator)));
        if (Operator.Trim().Length > AnnotationStore.OperatorLimit)
            return Task.FromResult(Invalid($"操作人不能超过 {AnnotationStore.OperatorLimit} 个字符", nameof(Operator)));
        if (Note.Length > AnnotationStore.NoteLimit)
            return Task.FromResult(Invalid($"备注不能超过 {AnnotationStore.NoteLimit} 个字符", nameof(Note)));
        if (Category.Trim().Length > AnnotationStore.NameLimit || Plan.Trim().Length > AnnotationStore.NameLimit)
            return Task.FromResult(Invalid($"取值不能超过 {AnnotationStore.NameLimit} 个字符"));
        var record = new RemoteAnnotation(context.FolderPath, CategoryName: Category.Trim(), PlanName: Plan.Trim(),
            Note: Note, Operator: Operator.Trim(), ImageName: ImageName);
        return WriteAsync(AnnotationWriteKind.Submit, async ct =>
        {
            await client.SaveAsync(record, ct);
            if (!disposed && !ct.IsCancellationRequested) saveOperator?.Invoke(record.Operator!);
        });
    }

    public Task<bool> RevokeAsync()
    {
        if (disposed || IsBusy || History.Count == 0) return Task.FromResult(false);
        string image = ImageName;
        return WriteAsync(AnnotationWriteKind.Revoke, async ct => { await client.DeleteImageAsync(context.FolderPath, image, ct); });
    }

    private async Task<bool> WriteAsync(AnnotationWriteKind kind, Func<CancellationToken, Task> write)
    {
        int version = imageVersion;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, selection.Token);
        PendingWrite = kind;
        IsWriting = true;
        ClearError();
        try
        {
            await write(cancel.Token);
            if (disposed || cancel.IsCancellationRequested || version != imageVersion) return false;
            IsDirty = false;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return false; }
        catch (Exception ex) { return disposed || version != imageVersion ? false : Invalid(ex.Message); }
        finally { IsWriting = false; }
        WriteSucceeded?.Invoke(kind);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); selection.Cancel();
        lifetime.Dispose(); selection.Dispose();
    }
}
