using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

public sealed class AnnotationDictionaryCache(Func<CancellationToken, Task<RemoteDictionary>> fetch, TimeProvider? clock = null) : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly CancellationTokenSource session = new();
    private RemoteDictionary? cached;
    private long cachedAt;
    private TaskCompletionSource<RemoteDictionary>? pending;
    private bool disposed;

    public Task<RemoteDictionary> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCompletionSource<RemoteDictionary> read;
        CancellationToken sessionToken;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cached is { } words && time.GetElapsedTime(cachedAt) < Lifetime) return Task.FromResult(words);
            if (pending is not null) return pending.Task.WaitAsync(ct);
            read = pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sessionToken = session.Token;
        }
        _ = LoadAsync(read, sessionToken);
        return read.Task.WaitAsync(ct);
    }

    public void Replace(RemoteDictionary words)
    {
        lock (gate)
        {
            if (disposed) return;
            cached = words;
            cachedAt = time.GetTimestamp();
            pending?.TrySetResult(words);
            pending = null;
        }
    }

    private async Task LoadAsync(TaskCompletionSource<RemoteDictionary> read, CancellationToken ct)
    {
        try
        {
            var words = await fetch(ct).ConfigureAwait(false);
            lock (gate)
            {
                if (pending != read) return;
                cached = words;
                cachedAt = time.GetTimestamp();
                pending = null;
                read.TrySetResult(words);
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (pending != read) return;
                pending = null;
                read.TrySetException(ex);
                _ = read.Task.Exception;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending?.TrySetCanceled();
            pending = null;
            cached = null;
        }
        session.Cancel();
        session.Dispose();
    }
}
