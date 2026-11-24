namespace Transfer.Core.Remote;

public record RemoteEntry(string Name, string Path, bool IsDirectory, long Length, DateTime CreatedUtc, DateTime ModifiedUtc)
{
    public string Kind => IsDirectory ? "文件夹" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public string Size => IsDirectory ? "" : $"{Length / 1024d:N1} KB";
    public DateTime CreatedLocal => CreatedUtc.ToLocalTime();
    public DateTime ModifiedLocal => ModifiedUtc.ToLocalTime();
    public bool IsText => Kind is ".txt" or ".log" or ".json";
    public bool IsImage => Kind is ".jpg" or ".png";
    public bool IsExe => Kind == ".exe";
    public bool IsZip => Kind == ".zip";

    // 浏览器为批次文件夹或图片填充的标注状态。
    public string AnnotationStatus { get; set; } = "";
    public string AnnotationTip { get; set; } = "";

    // WPF 用文件条目作为选中项的哈希键。标注状态刷新时，条目的相等性和哈希必须保持稳定。
    public virtual bool Equals(RemoteEntry? other) => other is not null
        && EqualityContract == other.EqualityContract
        && Name == other.Name && Path == other.Path && IsDirectory == other.IsDirectory
        && Length == other.Length && CreatedUtc == other.CreatedUtc && ModifiedUtc == other.ModifiedUtc;

    public override int GetHashCode() => HashCode.Combine(EqualityContract, Name, Path, IsDirectory, Length, CreatedUtc, ModifiedUtc);
}
public record RemoteTransferProgress(string RequestId, string Path, string Stage, double Percent, long CompletedBytes = 0, long TotalBytes = 0);
public record ProcessState(int[] ProcessIds, string State, string? Detail = null);
/// <summary>
/// 一次对线的失败分析记录，对应 RCMDB.AutoLineFailureAnalysisLog。
/// 新记录按图片追加，ImageName 为空表示旧版目录级记录。
/// </summary>
public record RemoteAnnotation(string FolderPath,
    string? EquipmentNo = null, string? EquipmentType = null, DateTime? AutoLineDate = null,
    string? BatchNumber = null, string? CategoryName = null, string? PlanName = null,
    string? Note = null, string? Operator = null, DateTime? UpdatedAt = null, string? ImageName = null);
public record RemoteDictionaryItem(string Code, string Name);
public enum AnnotationDictionaryKind { Category, Plan }
public record RemoteDictionary(RemoteDictionaryItem[] Categories, RemoteDictionaryItem[] Plans);
public record RemoteBatchKey(DateTime Date, string Batch);
public record RemoteNgBatch(string Name, string Path, bool Annotated);
public record RemoteNgDay(DateTime Date, RemoteNgBatch[] Batches);
public record RemoteRequest(string Command, string Path = "", string? Text = null, string? Version = null,
    string? EncodingName = null, long Length = 0, string? Sha256 = null, bool Overwrite = false,
    string? Filter = null, int Skip = 0, int Take = 0, bool FoldersOnly = false, int Protocol = 0,
    string? CategoryCode = null, string? PlanCode = null, string? Note = null, string? Operator = null,
    string? ImageName = null, DateTime? StartDate = null, DateTime? EndDate = null)
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
}
public record RemoteResponse(bool Ok, string? Error = null, RemoteEntry[]? Entries = null, string? Text = null,
    string? Version = null, string? EncodingName = null, long Length = 0, ProcessState? Process = null, string? RootName = null,
    string? Stage = null, double Percent = 0, string? ArchivePath = null, long CompletedBytes = 0, long TotalBytes = 0,
    string? Warning = null, int Total = 0, int Protocol = 0, string? Sha256 = null,
    RemoteAnnotation? Annotation = null, RemoteAnnotation[]? Annotations = null, RemoteDictionary? Dictionary = null,
    RemoteNgDay[]? NgDays = null);
// 目录过滤扩展的返回值：过滤后的条目，以及可选的提示信息。
public record RemoteFilterResult(RemoteEntry[] Entries, string? Warning = null);
public record RemoteUpdateInfo(string Version, string FileName, string Sha256, long Length, string Notes);
// 远程文件管理协议版本；客户端和服务端不一致时给出提示。
public static class RemoteProtocol
{
    public const int Version = 2;
}
