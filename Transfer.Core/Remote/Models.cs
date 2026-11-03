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
}
public record RemoteTransferProgress(string RequestId, string Path, string Stage, double Percent, long CompletedBytes = 0, long TotalBytes = 0);
public record ProcessState(int[] ProcessIds, string State, string? Detail = null);
public record RemoteRequest(string Command, string Path = "", string? Text = null, string? Version = null,
    string? EncodingName = null, long Length = 0, string? Sha256 = null, bool Overwrite = false,
    string? Filter = null)
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
}
public record RemoteResponse(bool Ok, string? Error = null, RemoteEntry[]? Entries = null, string? Text = null,
    string? Version = null, string? EncodingName = null, long Length = 0, ProcessState? Process = null, string? RootName = null,
    string? Stage = null, double Percent = 0, string? ArchivePath = null, long CompletedBytes = 0, long TotalBytes = 0,
    string? Warning = null);
// 目录过滤扩展的返回值：过滤后的条目，以及可选的提示信息。
public record RemoteFilterResult(RemoteEntry[] Entries, string? Warning = null);
