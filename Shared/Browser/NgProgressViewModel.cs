using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

public enum NgProgressFilter { All, Pending, Annotated }

/// <summary>NG 进度的数据、查询和筛选状态；不依赖 WPF 控件。</summary>
public sealed class NgProgressViewModel : IDisposable
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy.MM.dd", "yyyyMMdd"];
    private readonly Dictionary<DateTime, bool> expanded = [];
    private CancellationTokenSource? active;
    private CancellationTokenSource? reconcile;
    private Func<CancellationToken, Task>? pendingReconcile;
    private int version;
    private NgProgressFilter filter;
    public string? Path { get; private set; }
    public RemoteNgDay[] Days { get; private set; } = [];
    public ObservableCollection<NgDayRow> Rows { get; } = [];
    public bool HasResult { get; private set; }
    public string Message { get; private set; } = "";
    public int Total => Days.Sum(day => day.Batches.Length);
    public int Annotated => Days.Sum(day => day.Batches.Count(batch => batch.Annotated));
    public int Pending => Total - Annotated;

    public static bool TryDate(string text, out DateTime date) => DateTime.TryParseExact(text.Trim(), DateFormats,
        CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    public static (DateTime Start, DateTime End) GetDatePreset(int daysBack, DateTime today) => (today.Date.AddDays(-daysBack), today.Date);
    public void SetPath(string? path)
    {
        if (Path == path) return;
        Invalidate(""); expanded.Clear(); Path = path;
    }
    public void Reset() { Invalidate(""); expanded.Clear(); Path = null; }
    public void Invalidate(string message)
    {
        CancelReconcile();
        version++; active?.Cancel();
        Clear(message);
    }
    private void Clear(string message)
    {
        foreach (var row in Rows) expanded[row.Date] = row.IsExpanded;
        HasResult = false; Days = []; Rows.Clear(); Message = message;
    }
    public void SetFilter(NgProgressFilter value)
    {
        filter = value;
        if (HasResult) Render();
    }
    public void RememberExpanded(DateTime date, bool value) => expanded[date] = value;
    public void SetBatchAnnotated(string folder, bool annotated)
    {
        for (int dayIndex = 0; dayIndex < Days.Length; dayIndex++)
        {
            var day = Days[dayIndex];
            int index = Array.FindIndex(day.Batches, batch => batch.Path == folder);
            if (index < 0) continue;
            if (day.Batches[index].Annotated == annotated) return;
            var batches = (RemoteNgBatch[])day.Batches.Clone();
            batches[index] = batches[index] with { Annotated = annotated };
            Days[dayIndex] = day with { Batches = batches };
            Render();
            return;
        }
    }

    public void InvalidateQueryResponse() => version++;

    public void QueueReconcile(Func<CancellationToken, Task> refresh)
    {
        pendingReconcile = refresh;
        if (reconcile is null) _ = RunReconcileAsync();
    }

    public void CancelReconcile()
    {
        pendingReconcile = null;
        reconcile?.Cancel();
        reconcile = null;
    }

    private async Task RunReconcileAsync()
    {
        using var source = reconcile = new CancellationTokenSource();
        try
        {
            while (pendingReconcile is not null)
            {
                await Task.Delay(150, source.Token);
                if (pendingReconcile is not { } refresh) break;
                pendingReconcile = null;
                await refresh(source.Token);
                source.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex) { Log.Debug(ex, "后台校准 NG 总览失败"); }
        finally
        {
            if (ReferenceEquals(reconcile, source)) reconcile = null;
        }
    }
    public RemoteNgBatch? FindNext(string completedFolder)
    {
        var batches = Days.SelectMany(day => day.Batches).ToArray();
        int index = Array.FindIndex(batches, batch => batch.Path == completedFolder);
        return batches.Skip(index + 1).Concat(batches.Take(index + 1)).FirstOrDefault(batch => !batch.Annotated);
    }

    public async Task<bool> QueryAsync(string startText, string endText,
        Func<string, DateTime, DateTime, CancellationToken, Task<RemoteNgDay[]>> query,
        CancellationToken ct = default, Func<bool>? isCurrent = null, bool preserveResultOnFailure = false)
    {
        if (Path is not { } path) return false;
        if (!TryDate(startText, out var start) || !TryDate(endText, out var end)
            || end < start || (end - start).TotalDays >= 31)
        {
            Invalidate("请输入有效日期，格式为 yyyy-MM-dd，范围最多 31 天");
            return false;
        }
        active?.Cancel();
        var cancel = active = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int requested = ++version;
        bool Current() => requested == version && Path == path && (isCurrent?.Invoke() ?? true);
        try
        {
            var result = await query(path, start, end, cancel.Token);
            ct.ThrowIfCancellationRequested();
            if (!Current() || cancel.IsCancellationRequested) return false;
            Days = result; HasResult = true; Render();
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cancel.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (Current())
            {
                if (!preserveResultOnFailure) Clear("读取 NG 总览失败：" + ex.Message);
                Log.Warning(ex, "读取 NG 总览失败；目录 {RemoteDirectory}", path);
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(active, cancel)) active = null;
            cancel.Dispose();
        }
    }

    private void Render()
    {
        var previous = Rows.ToDictionary(day => day.Date);
        foreach (var day in Rows) expanded[day.Date] = day.IsExpanded;
        var next = new List<NgDayRow>();
        foreach (var day in Days)
        {
            var batches = filter == NgProgressFilter.All ? day.Batches
                : day.Batches.Where(batch => batch.Annotated == (filter == NgProgressFilter.Annotated)).ToArray();
            if (batches.Length == 0) continue;
            if (previous.TryGetValue(day.Date, out var row)) row.Update(day, batches);
            else row = new(day, batches, expanded.GetValueOrDefault(day.Date, next.Count == 0 && expanded.Count == 0));
            next.Add(row);
        }
        for (int index = 0; index < next.Count; index++)
        {
            var row = next[index];
            if (index >= Rows.Count) Rows.Add(row);
            else if (!ReferenceEquals(Rows[index], row))
            {
                int existing = Rows.IndexOf(row);
                if (existing < 0) Rows.Insert(index, row);
                else Rows.Move(existing, index);
            }
        }
        while (Rows.Count > next.Count) Rows.RemoveAt(Rows.Count - 1);
        Message = Rows.Count > 0 ? "" : Days.Length == 0 ? "所选日期范围内没有可查看的 NG 批次" : "没有符合筛选条件的 NG 批次";
    }
    public void Dispose() { Reset(); }
}
