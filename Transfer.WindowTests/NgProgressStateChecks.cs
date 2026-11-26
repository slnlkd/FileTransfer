extern alias RemoteClient;
using Transfer.Core.Remote;
using NgProgress = RemoteClient::RemoteFiles.Desktop.NgProgressViewModel;
using NgFilter = RemoteClient::RemoteFiles.Desktop.NgProgressFilter;

internal static class NgProgressStateChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        RemoteNgDay[] days = [new(new(2026, 11, 20), [new("1", "day/1", false), new("2", "day/2", true)]),
            new(new(2026, 11, 19), [new("3", "day/3", false)])];
        using var state = new NgProgress();
        state.SetPath("A601");
        await state.QueryAsync("2026-11-19", "2026-11-20", (_, _, _, _) => Task.FromResult(days));
        Check(state.Total == 3 && state.Pending == 2 && state.Annotated == 1 && state.Rows[0].IsExpanded, "初次查询应计算全部状态并展开最近日期");
        var rows = state.Rows; var first = rows[0]; var batches = first.VisibleBatches;
        first.IsExpanded = false;
        days = [days[0] with { Batches = [days[0].Batches[0] with { Annotated = true }, days[0].Batches[1]] }, days[1]];
        await state.QueryAsync("2026-11-19", "2026-11-20", (_, _, _, _) => Task.FromResult(days));
        Check(ReferenceEquals(state.Rows, rows) && ReferenceEquals(state.Rows[0], first) && ReferenceEquals(first.VisibleBatches, batches)
            && !first.IsExpanded && state.Annotated == 2, "刷新应复用日期与批次容器并保留展开状态");
        state.SetFilter(NgFilter.Pending);
        Check(state.Rows.Count == 1 && state.Rows[0].Date.Day == 19 && state.Total == 3, "筛选只能改变可见批次，不能改变总数");
        Check(state.FindNext("day/1")?.Path == "day/3", "下一批应跳过已标注并跨日期");
        state.Invalidate("日期已更改，请点击查询"); state.SetFilter(NgFilter.Annotated);
        Check(!state.HasResult && state.Rows.Count == 0 && state.Message.Contains("日期已更改"), "修改日期后筛选不能恢复旧结果");
        int calls = 0;
        await state.QueryAsync("2026-11-31", "2026-11-20", (_, _, _, _) => { calls++; return Task.FromResult(days); });
        Check(calls == 0 && state.Message.Contains("有效日期"), "无效日期不应查询服务端");
        Check(NgProgress.GetDatePreset(6, new(2026, 11, 26)).Start == new DateTime(2026, 11, 20), "近七天必须包含今天");
        var gate = new TaskCompletionSource<RemoteNgDay[]>();
        var old = state.QueryAsync("2026-11-19", "2026-11-20", (_, _, _, _) => gate.Task);
        state.SetPath("A602");
        state.SetFilter(NgFilter.All);
        await state.QueryAsync("2026-11-19", "2026-11-20", (_, _, _, _) => Task.FromResult(Array.Empty<RemoteNgDay>()));
        gate.SetResult(days); await old;
        Check(state.Path == "A602" && state.Total == 0 && state.Rows.Count == 0, "旧机台响应不能覆盖新机台");
        var staleFailure = new TaskCompletionSource<RemoteNgDay[]>();
        var failed = state.QueryAsync("2026-11-19", "2026-11-20", (_, _, _, _) => staleFailure.Task);
        state.Invalidate("日期已更改，请点击查询");
        staleFailure.SetException(new InvalidOperationException("旧查询失败"));
        await failed;
        Check(state.Message.Contains("日期已更改"), "迟到的查询错误不能覆盖当前日期提示");
        Console.WriteLine("通过：NG 日期校验、快捷范围、筛选计数、容器复用、下一批及旧响应隔离");
    }
}
