extern alias RemoteClient;
using Transfer.Core.Remote;
using NavigationState = RemoteClient::RemoteFiles.Desktop.DirectoryNavigationState;
using IDirectoryClient = RemoteClient::RemoteFiles.Desktop.IDirectoryClient;

internal static class BrowserStateChecks
{
    public static async Task RunNavigationAsync()
    {
        static RemoteEntry Row(string path) => new(path, path, true, 0, default, default);
        static RemoteResponse Response(params string[] paths) => new(true, Entries: paths.Select(Row).ToArray(), Total: paths.Length);
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var requests = new List<RemoteRequest>();
        var old = new TaskCompletionSource<RemoteResponse>();
        var source = new FakeDirectoryClient((request, _) =>
        {
            requests.Add(request);
            return request.Path == "old" ? old.Task : Task.FromResult(Response(request.Path + "/child"));
        });
        using var state = new NavigationState();
        state.SetClient(source);
        var first = state.NavigateAsync("old", null);
        await state.NavigateAsync("new", null);
        old.SetResult(Response("old/child"));
        Check(await first is null && state.Path == "new" && state.Entries.Single().Path == "new/child", "旧导航不能覆盖新目录");
        Check(requests[0].Take == 500, "首屏分页大小必须保持 500");
        var failed = new TaskCompletionSource<RemoteResponse>();
        state.SetClient(new FakeDirectoryClient((request, _) => request.Path == "fail" ? failed.Task : Task.FromResult(Response("ok"))));
        var failedNavigation = state.NavigateAsync("fail", null);
        await state.NavigateAsync("ok", null);
        failed.SetException(new InvalidOperationException("旧请求错误"));
        Check(await failedNavigation is null && state.Path == "ok", "旧导航失败不能影响当前目录或状态");
        var stale = new TaskCompletionSource<RemoteResponse>();
        state.SetClient(new FakeDirectoryClient((_, _) => stale.Task));
        var pending = state.NavigateAsync("stale", null);
        state.SetClient(source);
        stale.SetResult(Response("stale"));
        Check(await pending is null && state.Entries.Count == 0, "重连后旧请求不能恢复旧数据");
        int pageCalls = 0;
        var pageGate = new TaskCompletionSource<RemoteResponse>();
        state.SetClient(new FakeDirectoryClient((request, _) =>
        {
            pageCalls++;
            return request.Skip == 0 ? Task.FromResult(new RemoteResponse(true, Entries: [Row("1"), Row("2")], Total: 3)) : pageGate.Task;
        }));
        await state.NavigateAsync("paged", "ng");
        var more = state.LoadMoreAsync();
        var duplicate = state.LoadMoreAsync();
        pageGate.SetResult(Response("3"));
        await Task.WhenAll(more, duplicate);
        Check(pageCalls == 2 && state.Entries.Count == 3 && state.AllLoaded, "分页请求必须去重且每页只能追加一次");
        foreach (bool cancelNew in new[] { false, true })
        {
            int unexpectedPages = 0;
            var firstPage = new TaskCompletionSource<RemoteResponse>();
            state.SetClient(new FakeDirectoryClient((request, _) =>
            {
                if (request.Path == "B") return firstPage.Task;
                if (request.Skip > 0) { unexpectedPages++; return Task.FromResult(new RemoteResponse(true, Entries: [Row("A/3")], Total: 3)); }
                return Task.FromResult(new RemoteResponse(true, Entries: [Row("A/1"), Row("A/2")], Total: 3));
            }));
            await state.NavigateAsync("A", null);
            int listingVersion = state.ListingVersion;
            using var cancelNewNavigation = new CancellationTokenSource();
            var switching = state.NavigateAsync("B", null, cancelNewNavigation.Token);
            await state.LoadMoreAsync();
            Check(unexpectedPages == 0, "新目录首屏未返回前不得发起旧目录分页");
            if (cancelNew) cancelNewNavigation.Cancel();
            firstPage.SetResult(Response("B/only"));
            try { await switching; } catch (OperationCanceledException) when (cancelNew) { }
            if (cancelNew)
            {
                await state.LoadMoreAsync();
                Check(state.Path == "A" && state.Entries.Count == 3 && unexpectedPages == 1,
                    "取消新导航后，仍显示的旧目录必须可以继续分页");
                Check(state.ListingVersion == listingVersion, "取消导航应保留原列表身份，避免丢失查看器关闭回调");
            }
            else Check(state.Path == "B" && state.Entries.Single().Path == "B/only", "新目录不能混入旧分页");
        }
        int cacheCalls = 0;
        var now = DateTime.UtcNow;
        using var cached = new NavigationState(() => now);
        cached.SetClient(new FakeDirectoryClient((_, _) => { cacheCalls++; return Task.FromResult(Response("cached")); }));
        await cached.NavigateAsync("cache", null);
        await cached.GetUnfilteredAsync("cache");
        Check(cacheCalls == 1, "完整未过滤首屏应复用给目录树");
        now = now.AddSeconds(11); await cached.GetUnfilteredAsync("cache");
        Check(cacheCalls == 2, "缓存应在 10 秒后失效");
        cached.InvalidateCache(); await cached.GetUnfilteredAsync("cache");
        Check(cacheCalls == 3, "文件写入后的缓存失效应强制读取");
        var cacheGate = new TaskCompletionSource<RemoteResponse>();
        cached.SetClient(new FakeDirectoryClient((_, _) => cacheGate.Task));
        var read = cached.GetUnfilteredAsync("shared");
        var secondRead = cached.GetUnfilteredAsync("shared");
        cached.SetClient(new FakeDirectoryClient((_, _) => Task.FromResult(Response("new-server"))));
        cacheGate.SetResult(Response("old-server"));
        try { await Task.WhenAll(read, secondRead); } catch (OperationCanceledException) { }
        Check((await cached.GetUnfilteredAsync("shared")).Single().Path == "new-server", "旧连接的树请求不能污染新缓存");
        var cancelledReply = new TaskCompletionSource<RemoteResponse>();
        cached.SetClient(new FakeDirectoryClient((_, _) => cancelledReply.Task));
        using var cancel = new CancellationTokenSource();
        var cancelledNavigation = cached.NavigateAsync("cancelled", null, cancel.Token);
        cancel.Cancel(); cancelledReply.SetResult(Response("must-not-apply"));
        bool observedCancel = false;
        try { await cancelledNavigation; } catch (OperationCanceledException) { observedCancel = true; }
        Check(observedCancel && cached.Entries.Count == 0, "取消导航不得提交数据，且应通知调用方");
        int reads = 0;
        var abandoned = new TaskCompletionSource<RemoteResponse>();
        cached.SetClient(new FakeDirectoryClient((_, _) => ++reads == 1 ? abandoned.Task : Task.FromResult(Response("fresh"))));
        using var cancelWait = new CancellationTokenSource();
        var waiting = cached.GetUnfilteredAsync("abandoned", cancelWait.Token);
        cancelWait.Cancel();
        try { await waiting; } catch (OperationCanceledException) { }
        abandoned.SetResult(Response("expired"));
        await Task.Yield(); now = now.AddSeconds(11);
        Check((await cached.GetUnfilteredAsync("abandoned")).Single().Path == "fresh" && reads == 2,
            "取消唯一等待者后，后台请求完成也必须移除任务，否则过期缓存会被重复读取");
        Console.WriteLine("通过：目录导航版本、重连、分页去重、未过滤缓存复用与过期保护");
    }
    private sealed class FakeDirectoryClient(Func<RemoteRequest, CancellationToken, Task<RemoteResponse>> call) : IDirectoryClient
    {
        public Task<RemoteResponse> ListAsync(RemoteRequest request, CancellationToken ct) => call(request, ct);
    }
}
