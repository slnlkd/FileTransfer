using System.Collections.ObjectModel;
using System.IO;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

public interface IDirectoryClient
{
    Task<RemoteResponse> ListAsync(RemoteRequest request, CancellationToken ct);
}

public sealed class RemoteDirectoryClient(RemoteFileClient client) : IDirectoryClient
{
    public Task<RemoteResponse> ListAsync(RemoteRequest request, CancellationToken ct) => Task.Run(() => client.CallAsync(request, ct), ct);
}

/// <summary>由 UI 上下文串行使用；管理数据、请求版本与缓存，不持有控件。</summary>
public sealed class DirectoryNavigationState(Func<DateTime>? utcNow = null) : IDisposable
{
    public const int PageSize = 500;
    private readonly Func<DateTime> now = utcNow ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, (DateTime At, RemoteEntry[] Entries)> cache = [];
    private readonly Dictionary<string, Task<RemoteEntry[]>> reads = [];
    private IDirectoryClient? client;
    private CancellationTokenSource session = new();
    private CancellationTokenSource? navigation;
    private string? filter;
    private int cacheVersion;
    private bool navigating, disposed;
    public event Action<RemoteEntry[]>? PageReceived;
    public string Path { get; private set; } = "";
    public string? RootName { get; private set; }
    public ObservableCollection<RemoteEntry> Entries { get; private set; } = [];
    public int Total { get; private set; }
    public bool AllLoaded { get; private set; } = true;
    public int Version { get; private set; }
    // 界面回调绑定已呈现的列表；失败/取消的新请求不能使原列表身份失效。
    public int ListingVersion { get; private set; }
    public Task PageLoad { get; private set; } = Task.CompletedTask;

    public void SetClient(IDirectoryClient? endpoint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        session.Cancel(); session.Dispose(); session = new();
        navigation?.Cancel(); navigation?.Dispose(); navigation = null;
        client = endpoint;
        Version++;
        ListingVersion++;
        Path = ""; RootName = null; Entries = []; Total = 0; AllLoaded = true;
        PageLoad = Task.CompletedTask;
        navigating = false;
        InvalidateCache();
    }

    public void InvalidateCache() { cacheVersion++; cache.Clear(); reads.Clear(); }

    public async Task<RemoteResponse?> NavigateAsync(string path, string? value, CancellationToken ct = default)
    {
        var endpoint = client ?? throw new IOException("请先连接服务端");
        navigation?.Cancel(); navigation?.Dispose();
        var cancel = navigation = CancellationTokenSource.CreateLinkedTokenSource(session.Token, ct);
        int version = ++Version;
        int cachedAtVersion = cacheVersion;
        PageLoad = Task.CompletedTask;
        navigating = true;
        try
        {
            var response = await endpoint.ListAsync(new("list", path, Filter: value, Take: PageSize), cancel.Token);
            ct.ThrowIfCancellationRequested();
            if (cancel.IsCancellationRequested || version != Version || !ReferenceEquals(client, endpoint)) return null;
            var page = response.Entries ?? [];
            Path = path; filter = value; RootName = response.RootName;
            ListingVersion++;
            Entries = new(page);
            Total = response.Total > 0 ? response.Total : page.Length;
            AllLoaded = page.Length >= Total;
            if (string.IsNullOrEmpty(value) && AllLoaded && cachedAtVersion == cacheVersion) cache[path] = (now(), page);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cancel.IsCancellationRequested) { return null; }
        catch (Exception) when (!ct.IsCancellationRequested && (version != Version || !ReferenceEquals(client, endpoint))) { return null; }
        finally { if (version == Version) navigating = false; }
    }

    public Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!PageLoad.IsCompleted) return PageLoad;
        if (navigating || AllLoaded || client is null) return Task.CompletedTask;
        return PageLoad = LoadPageAsync(client, Version, ct);
    }

    private async Task LoadPageAsync(IDirectoryClient endpoint, int version, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(session.Token, ct);
        var target = Entries;
        string path = Path;
        int cachedAtVersion = cacheVersion;
        try
        {
            var response = await endpoint.ListAsync(new("list", path, Filter: filter, Skip: target.Count, Take: PageSize), cancel.Token);
            ct.ThrowIfCancellationRequested();
            if (cancel.IsCancellationRequested || version != Version || !ReferenceEquals(client, endpoint) || !ReferenceEquals(Entries, target)) return;
            var page = response.Entries ?? [];
            if (response.Total > 0) Total = response.Total;
            PageReceived?.Invoke(page);
            if (version != Version || !ReferenceEquals(Entries, target)) return;
            foreach (var entry in page) target.Add(entry);
            AllLoaded = page.Length == 0 || Entries.Count >= Total;
            if (AllLoaded && Entries.Count >= Total && string.IsNullOrEmpty(filter) && cachedAtVersion == cacheVersion)
                cache[path] = (now(), target.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && (version != Version || cancel.IsCancellationRequested)) { }
        catch (Exception) when (!ct.IsCancellationRequested && (version != Version || !ReferenceEquals(client, endpoint))) { }
    }

    public Task<RemoteEntry[]> GetUnfilteredAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (cache.TryGetValue(path, out var hit) && now() - hit.At < TimeSpan.FromSeconds(10)) return Task.FromResult(hit.Entries);
        if (!reads.TryGetValue(path, out var read))
        {
            var endpoint = client ?? throw new IOException("请先连接服务端");
            var completion = new TaskCompletionSource<RemoteEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            read = completion.Task;
            reads[path] = read;
            _ = CompleteReadAsync(endpoint, path, cacheVersion, session.Token, completion);
        }
        return read.WaitAsync(ct);
    }

    private async Task CompleteReadAsync(IDirectoryClient endpoint, string path, int version, CancellationToken ct,
        TaskCompletionSource<RemoteEntry[]> completion)
    {
        try { completion.TrySetResult(await ReadUnfilteredAsync(endpoint, path, version, ct)); }
        catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            _ = completion.Task.Exception; // 即使所有等待者均取消，也观察后台故障。
        }
        finally
        {
            if (reads.GetValueOrDefault(path) == completion.Task) reads.Remove(path);
        }
    }

    private async Task<RemoteEntry[]> ReadUnfilteredAsync(IDirectoryClient endpoint, string path, int version, CancellationToken ct)
    {
        try
        {
            var response = await endpoint.ListAsync(new("list", path), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(client, endpoint) || version != cacheVersion)
                throw new OperationCanceledException("目录结果已失效", ct);
            var result = response.Entries ?? [];
            cache[path] = (now(), result);
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested || !ReferenceEquals(client, endpoint) || version != cacheVersion)
        { throw new OperationCanceledException("目录结果已失效", ct); }
    }

    public void Dispose()
    {
        if (disposed) return;
        SetClient(null);
        disposed = true;
        session.Dispose();
    }
}
