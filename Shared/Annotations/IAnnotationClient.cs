using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>标注模块的远程边界；不暴露图片查看器或 WPF 控件。</summary>
public interface IAnnotationClient
{
    Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct);
    Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct);
    Task<RemoteAnnotation[]> GetHistoryAsync(string folder, CancellationToken ct);
    Task<RemoteAnnotation> SaveAsync(RemoteAnnotation record, CancellationToken ct);
    Task<int> DeleteImageAsync(string folder, string imageName, CancellationToken ct);
}

public sealed class RemoteAnnotationClient(RemoteFileClient client, string? ngResultId = null,
    AnnotationDictionaryCache? dictionaryCache = null) : IAnnotationClient
{
    public Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct) =>
        dictionaryCache?.GetAsync(ct) ?? client.GetAnnotationDictionaryAsync(ct);
    public async Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct)
    {
        var words = await client.AddAnnotationDictionaryItemAsync(kind, name, ct);
        dictionaryCache?.Replace(words);
        return words;
    }
    public Task<RemoteAnnotation[]> GetHistoryAsync(string folder, CancellationToken ct) => client.GetAnnotationHistoryAsync(folder, ct);
    public Task<RemoteAnnotation> SaveAsync(RemoteAnnotation record, CancellationToken ct) =>
        client.SaveAnnotationAsync(record.FolderPath, record.CategoryName ?? "", record.PlanName ?? "",
            record.Note, record.Operator ?? "", record.ImageName ?? "", ct, ngResultId, record.CategoryDictionaryId);
    public Task<int> DeleteImageAsync(string folder, string imageName, CancellationToken ct) =>
        client.DeleteImageAnnotationsAsync(folder, imageName, ct, ngResultId);
}
