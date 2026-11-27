using System.Diagnostics;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>同一浏览器连接中的标注加载、字典复用和撤销结果读取。</summary>
internal sealed class BrowserAnnotationWorkflow(RemoteFileClient endpoint) : IDisposable
{
    private readonly AnnotationDictionaryCache dictionary = new(endpoint.GetAnnotationDictionaryAsync);
    private readonly Dictionary<string, object> pendingRevocations = new(StringComparer.Ordinal);

    internal sealed record WindowData(RemoteEntry[] Images, AnnotationContext Context, int HistoryCount);
    internal sealed record BatchState(RemoteAnnotation[] History, bool HasAnnotations);

    public async Task<WindowData> LoadAsync(DirectoryNavigationState navigation, string folder, bool fromNg)
    {
        long started = Stopwatch.GetTimestamp();
        string displayPath = (navigation.RootName ?? "共享根目录") + "/" + folder;
        var listing = navigation.GetUnfilteredAsync(folder);
        var historyRead = ReadHistoryAsync(folder);
        var targetRead = fromNg ? ReadTargetAsync(folder) : Task.FromResult<RemoteAnnotationTarget?>(null);
        await Task.WhenAll(listing, historyRead, targetRead);

        var (history, historyError) = await historyRead;
        var context = new AnnotationContext(folder, displayPath, history)
        {
            HistoryReadError = historyError,
            Target = await targetRead,
            OpenStartedAt = started,
            DictionaryCache = dictionary
        };
        return new((await listing).Where(entry => entry.IsImage).ToArray(), context, history.Length);
    }

    private async Task<(RemoteAnnotation[] History, string? Error)> ReadHistoryAsync(string folder)
    {
        try { return (await endpoint.GetAnnotationHistoryAsync(folder), null); }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取对线标注失败；目录 {RemoteDirectory}", folder);
            return ([], "读取标注历史失败：" + ex.Message);
        }
    }

    private async Task<RemoteAnnotationTarget?> ReadTargetAsync(string folder)
    {
        try { return await endpoint.GetAnnotationTargetAsync(folder); }
        catch (Exception ex)
        {
            Log.Warning(ex, "定位 NG 结果失败；目录 {RemoteDirectory}", folder);
            return new(Error: "定位 NG 结果失败：" + ex.Message);
        }
    }

    public void InvalidateRevocation(string folder) => pendingRevocations.Remove(folder);
    public void InvalidateRevocations() => pendingRevocations.Clear();

    public async Task<BatchState?> ReadAfterRevokeAsync(string folder)
    {
        object revision = new();
        pendingRevocations[folder] = revision;
        try
        {
            var (history, hasAnnotations) = await endpoint.GetAnnotationHistoryStateAsync(folder);
            return pendingRevocations.GetValueOrDefault(folder) == revision
                ? new(history, hasAnnotations ?? history.Length > 0) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "撤销后读取批次标注失败；目录 {RemoteDirectory}", folder);
            return null;
        }
        finally
        {
            if (pendingRevocations.GetValueOrDefault(folder) == revision) pendingRevocations.Remove(folder);
        }
    }

    public void Dispose()
    {
        InvalidateRevocations();
        dictionary.Dispose();
    }
}
