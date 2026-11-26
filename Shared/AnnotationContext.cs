using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>
/// 标注模式上下文，按图片名查找当前批次的历史记录。
/// </summary>
public sealed class AnnotationContext(string folderPath, string displayPath, IEnumerable<RemoteAnnotation> history)
{
    private readonly Dictionary<string, RemoteAnnotation[]> byImage = history
        .Where(item => !string.IsNullOrEmpty(item.ImageName))
        .GroupBy(item => item.ImageName!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    public string FolderPath { get; } = folderPath;
    public string DisplayPath { get; } = displayPath;
    public string? HistoryReadError { get; init; }
    public IReadOnlyList<RemoteAnnotation> GetImageHistory(string imageName) => byImage.GetValueOrDefault(imageName) ?? [];

    // Oracle DATE 保留数据库的本地钟面时间，只有明确标记为 UTC 的时间需要转换。
    public static DateTime? GetDisplayTime(DateTime? time) => time is { Kind: DateTimeKind.Utc } utc ? utc.ToLocalTime() : time;
}
