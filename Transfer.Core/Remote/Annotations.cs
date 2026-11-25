using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Transfer.Core.Remote;

/// <summary>
/// 对线批次目录名归一化：目录可能叫 "53"，也可能叫 "181_53"（存图规则带上机台前缀），
/// 数据库里的 Parameter2/JOB_BATCH_NUMBER 目前两种格式都可能存在，统一按最后一个下划线后的编号比较。
/// </summary>
public static class BatchNames
{
    public static string Normalize(string? value)
    {
        string text = (value ?? "").Trim();
        int index = text.LastIndexOf('_');
        return index >= 0 && index < text.Length - 1 ? text[(index + 1)..] : text;
    }
}

/// <summary>标注仓储：只负责存取，校验、路径解析在 <see cref="AnnotationStore"/>。</summary>
public interface IAnnotationRepository
{
    // 按标注时间从新到旧返回；batch 为空时返回整天的记录。
    Task<RemoteAnnotation[]> ListAsync(string equipmentNo, DateTime date, CancellationToken ct = default, string? batch = null);
    Task<RemoteBatchKey[]> ListAnnotatedBatchesAsync(string equipmentNo, DateTime start, DateTime end, CancellationToken ct);
    Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, CancellationToken ct = default);
    Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName, CancellationToken ct = default);
    Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct = default);
    Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct = default);
}

/// <summary>
/// 标注读写入口。每次提交追加一张图片的标注记录：
/// 支持 机台/yyyyMMdd/批次 和 DS616/机台/SaveImages/yyyyMMdd/批次，
/// 机台、日期、批次从路径解析后落到
/// AutoLineFailureAnalysisLog 的 EQP_ID / AUTOLINE_DATE / JOB_BATCH_NUMBER。
/// </summary>
public sealed class AnnotationStore(RemoteFileStore store, IAnnotationRepository repository)
{
    public const int NoteLimit = 500;
    public const int OperatorLimit = 100;
    public const int NameLimit = 200;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<RemoteNgDay[]> GetNgOverviewAsync(string path, DateTime? startDate, DateTime? endDate,
        Func<string, DateTime, DateTime, CancellationToken, Task<RemoteBatchKey[]>> query, CancellationToken ct)
    {
        if (startDate is null || endDate is null || endDate.Value.Date < startDate.Value.Date
            || (endDate.Value.Date - startDate.Value.Date).TotalDays >= 31 || endDate.Value.Date == DateTime.MaxValue.Date)
            throw new IOException("请选择有效的日期范围，最多 31 天");
        var start = DateTime.SpecifyKind(startDate.Value.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(endDate.Value.Date.AddDays(1), DateTimeKind.Unspecified);
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string equipment = parts.LastOrDefault() ?? "";
        bool imageRoot = equipment.Equals("SaveImages", StringComparison.OrdinalIgnoreCase);
        if (imageRoot) equipment = parts.Length > 1 ? parts[^2] : "";
        if (!equipment.StartsWith("A6", StringComparison.OrdinalIgnoreCase))
            throw new IOException("NG 总览需在机台目录或 SaveImages 目录打开");

        var dates = store.List(path).Where(item => item.IsDirectory).ToArray();
        var screenshots = !imageRoot ? dates.FirstOrDefault(item => item.Name.Equals("SaveImages", StringComparison.OrdinalIgnoreCase)) : null;
        if (screenshots is not null)
            dates = [.. store.List(screenshots.Path).Where(item => item.IsDirectory), .. dates];
        var dayFolders = dates.Select(item =>
            (item.Path, Date: DateTime.TryParseExact(item.Name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (DateTime?)date : null))
            .Where(item => item.Date >= start && item.Date < end).ToArray();
        if (dayFolders.Length == 0) return [];

        var ng = (await query(equipment, start, end, ct)).ToLookup(item => item.Date.Date, item => BatchNames.Normalize(item.Batch));
        var annotated = (await repository.ListAnnotatedBatchesAsync(equipment, start, end, ct))
            .ToLookup(item => item.Date.Date, item => BatchNames.Normalize(item.Batch));
        var days = new List<RemoteNgDay>();
        foreach (var day in dayFolders.GroupBy(item => item.Date!.Value).OrderByDescending(group => group.Key))
        {
            ct.ThrowIfCancellationRequested();
            var ngNames = ng[day.Key].ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ngNames.Count == 0) continue;
            var annotatedNames = annotated[day.Key].ToHashSet(StringComparer.OrdinalIgnoreCase);
            var batches = day.SelectMany(item => store.List(item.Path))
                .Where(item => item.IsDirectory && ngNames.Contains(BatchNames.Normalize(item.Name)))
                .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Name, NaturalComparer.Instance)
                .Select(item => new RemoteNgBatch(item.Name, item.Path, annotatedNames.Contains(BatchNames.Normalize(item.Name)))).ToArray();
            if (batches.Length > 0) days.Add(new(day.Key, batches));
        }
        return days.ToArray();
    }

    public async Task<RemoteDictionary> DictionaryAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            // 多台服务端共享字典表；进入标注页面时读取最新数据，避免其他节点新增后仍返回旧缓存。
            return await repository.GetDictionaryAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task<RemoteDictionary> AddDictionaryItemAsync(string group, string? text, CancellationToken ct = default)
    {
        var kind = group switch
        {
            "category" => AnnotationDictionaryKind.Category,
            "plan" => AnnotationDictionaryKind.Plan,
            "remark" => AnnotationDictionaryKind.Remark,
            _ => throw new IOException("不支持的标注字典分组")
        };
        string name = (text ?? "").Trim();
        if (name.Length == 0) throw new IOException("请填写字典项名称");
        if (name.Length > NameLimit) throw new IOException($"字典项名称不能超过 {NameLimit} 个字符");
        if (name.Any(char.IsControl)) throw new IOException("字典项名称不能包含控制字符");

        await gate.WaitAsync(ct);
        try
        {
            return await repository.AddDictionaryItemAsync(kind, name, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>读取一个对线文件夹的标注历史。</summary>
    public async Task<RemoteAnnotation[]> GetHistoryAsync(string folderPath, CancellationToken ct = default)
    {
        var key = ParseFolder(folderPath);
        var history = await repository.ListAsync(key.Equipment, key.Date, ct, key.Batch);
        return history.Where(item => SameFolder(item.FolderPath, key.Normalized)
            || (string.IsNullOrEmpty(item.FolderPath) && string.IsNullOrEmpty(item.ImageName))).ToArray();
    }

    /// <summary>列出某台设备某天的全部对线标注，供列表的「标注状态」列使用。</summary>
    public Task<RemoteAnnotation[]> ListAsync(string dateDirectory, CancellationToken ct = default)
    {
        var key = ParseDateDirectory(dateDirectory);
        return repository.ListAsync(key.Equipment, key.Date, ct);
    }

    public async Task<RemoteAnnotation> SaveAsync(RemoteRequest request, CancellationToken ct = default)
    {
        var key = ParseFolder(request.Path);
        string category = (request.CategoryCode ?? "").Trim();
        string plan = (request.PlanCode ?? "").Trim();
        string note = (request.Note ?? "").Trim();
        string @operator = (request.Operator ?? "").Trim();
        string imageName = (request.ImageName ?? "").Trim();
        if (imageName.Length > 0) ValidateImageName(imageName);
        if (category.Length == 0) throw new IOException("请选择异常归类");
        if (plan.Length == 0) throw new IOException("请选择异常处理方案");
        if (@operator.Length == 0) throw new IOException("请填写操作人");
        if (@operator.Length > OperatorLimit) throw new IOException($"操作人不能超过 {OperatorLimit} 个字符");
        if (note.Length > NoteLimit) throw new IOException($"备注不能超过 {NoteLimit} 个字符");
        if (category.Length > NameLimit || plan.Length > NameLimit) throw new IOException($"取值不能超过 {NameLimit} 个字符");

        var words = await DictionaryAsync(ct);
        if (words.Categories.Length > 0 && words.Categories.All(item => !string.Equals(item.Name, category, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("异常归类不在字典中：" + category);
        if (words.Plans.Length > 0 && words.Plans.All(item => !string.Equals(item.Name, plan, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("异常处理方案不在字典中：" + plan);

        var annotation = new RemoteAnnotation(key.Normalized, key.Equipment, key.EquipmentType, key.Date, key.Batch,
            category, plan, note, @operator, DateTime.UtcNow, imageName.Length == 0 ? null : imageName);
        return await repository.SaveAsync(annotation, ct);
    }

    public Task<int> DeleteImageAsync(string folderPath, string? imageName, CancellationToken ct = default)
    {
        imageName = imageName?.Trim();
        if (string.IsNullOrEmpty(imageName)) throw new IOException("撤销标注必须指定图片");
        ValidateImageName(imageName);
        var key = ParseFolder(folderPath);
        return repository.DeleteImageAsync(key.Equipment, key.Date, key.Batch, key.Normalized, imageName, ct);
    }

    private static void ValidateImageName(string imageName)
    {
        if (imageName.IndexOfAny(['/', '\\']) >= 0) throw new IOException("图片名称不能包含路径分隔符");
        if (Path.GetExtension(imageName).ToLowerInvariant() is not (".jpg" or ".png")) throw new IOException("仅支持 jpg、png 图片");
    }

    // 目录名从纯编号（53）改成带服务器前缀（181_53）后，历史记录与新目录要能互相匹配：
    // 其余层级必须一致，最后一段按编号比较。
    internal static bool SameFolder(string? storedPath, string folderPath)
    {
        string stored = (storedPath ?? "").Replace('\\', '/').Trim('/');
        string target = (folderPath ?? "").Replace('\\', '/').Trim('/');
        if (string.Equals(stored, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (stored.Length == 0 || target.Length == 0) return false;
        string[] left = stored.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] right = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            bool last = i == left.Length - 1;
            bool equal = last
                ? BatchNames.Normalize(left[i]).Equals(BatchNames.Normalize(right[i]), StringComparison.OrdinalIgnoreCase)
                : string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase);
            if (!equal) return false;
        }
        return true;
    }

    private sealed record FolderKey(string Normalized, string Equipment, DateTime Date, string Batch, string? EquipmentType);

    private FolderKey ParseFolder(string relative) => ParsePath(relative, hasBatch: true);

    private FolderKey ParseDateDirectory(string relative) => ParsePath(relative, hasBatch: false);

    private FolderKey ParsePath(string relative, bool hasBatch)
    {
        string full = store.Resolve(relative);
        if (!Directory.Exists(full)) throw new IOException((hasBatch ? "对线文件夹不存在：" : "目录不存在：") + relative);
        string normalized = Path.GetRelativePath(store.Root, full).Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int dateLimit = segments.Length - (hasBatch ? 1 : 0);
        if (dateLimit < 2) throw new IOException((hasBatch ? "路径不符合 机台/日期/批次 结构：" : "路径不符合 机台/日期 结构：") + normalized);
        for (int i = 1; i < dateLimit; i++)
        {
            if (!DateTime.TryParseExact(segments[i], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            int equipmentIndex = i - 1;
            if (segments[equipmentIndex].Equals("SaveImages", StringComparison.OrdinalIgnoreCase))
            {
                if (equipmentIndex == 0) throw new IOException("SaveImages 前缺少机台目录：" + normalized);
                equipmentIndex--;
            }
            string modelDirectory = equipmentIndex > 0 ? segments[equipmentIndex - 1] : Path.GetFileName(store.Root);
            string? equipmentType = modelDirectory.Equals("DS616", StringComparison.OrdinalIgnoreCase) ? "DS616" : null;
            return new(normalized, segments[equipmentIndex], DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified),
                hasBatch ? segments[^1] : "", equipmentType);
        }
        throw new IOException("路径中没有 yyyyMMdd 日期目录：" + normalized);
    }
}

/// <summary>
/// 本地 JSON 仓储：没有 Oracle 时也能把标注流程跑通，同时供冒烟测试使用。
/// 文件里同时放字典（没有数据库时的兜底来源）。
/// </summary>
public sealed class JsonAnnotationRepository : IAnnotationRepository
{
    private sealed record Document(RemoteDictionary? Dictionary, RemoteAnnotation[] Annotations);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public JsonAnnotationRepository(string path) => this.path = path;

    public async Task<RemoteAnnotation[]> ListAsync(string equipmentNo, DateTime date, CancellationToken ct = default, string? batch = null)
    {
        var document = await ReadAsync(ct);
        return document.Annotations.AsEnumerable().Reverse()
            .Where(item => string.Equals(item.EquipmentNo, equipmentNo, StringComparison.OrdinalIgnoreCase)
                && item.AutoLineDate?.Date == date.Date
                // 新目录名带服务器前缀（181_53），历史记录是纯编号（53），两种都匹配。
                && (batch is null || string.Equals(item.BatchNumber, batch, StringComparison.OrdinalIgnoreCase)
                    || BatchNames.Normalize(item.BatchNumber).Equals(BatchNames.Normalize(batch), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    public async Task<RemoteBatchKey[]> ListAnnotatedBatchesAsync(string equipmentNo, DateTime start, DateTime end, CancellationToken ct)
    {
        var document = await ReadAsync(ct);
        return document.Annotations.Where(item => string.Equals(item.EquipmentNo, equipmentNo, StringComparison.OrdinalIgnoreCase)
            && item.AutoLineDate >= start && item.AutoLineDate < end && !string.IsNullOrWhiteSpace(item.BatchNumber))
            .Select(item => new RemoteBatchKey(item.AutoLineDate!.Value.Date, item.BatchNumber!)).Distinct().ToArray();
    }

    public async Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await ReadAsync(ct);
            await WriteAsync(new Document(document.Dictionary, [.. document.Annotations, annotation]), ct);
            return annotation;
        }
        finally { gate.Release(); }
    }

    public async Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct = default)
    {
        var document = await ReadAsync(ct);
        return document.Dictionary ?? new RemoteDictionary([], []);
    }

    public async Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await ReadAsync(ct);
            var dictionary = document.Dictionary ?? new RemoteDictionary([], []);
            var items = kind switch
            {
                AnnotationDictionaryKind.Category => dictionary.Categories,
                AnnotationDictionaryKind.Plan => dictionary.Plans,
                AnnotationDictionaryKind.Remark => dictionary.Remarks,
                _ => throw new IOException("不支持的标注字典分组")
            };
            if (items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) return dictionary;
            RemoteDictionaryItem[] updated = [.. items, new(name, name)];
            dictionary = kind switch
            {
                AnnotationDictionaryKind.Category => dictionary with { Categories = updated },
                AnnotationDictionaryKind.Plan => dictionary with { Plans = updated },
                _ => dictionary with { Remarks = updated }
            };
            await WriteAsync(document with { Dictionary = dictionary }, ct);
            return dictionary;
        }
        finally { gate.Release(); }
    }

    public async Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await ReadAsync(ct);
            var remaining = Array.FindAll(document.Annotations, item => !(
                string.Equals(item.EquipmentNo, equipmentNo, StringComparison.OrdinalIgnoreCase)
                && item.AutoLineDate?.Date == date.Date
                && string.Equals(item.BatchNumber, batch, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ImageName, imageName, StringComparison.OrdinalIgnoreCase)
                && AnnotationStore.SameFolder(item.FolderPath, folderPath)));
            int removed = document.Annotations.Length - remaining.Length;
            if (removed > 0) await WriteAsync(document with { Annotations = remaining }, ct);
            return removed;
        }
        finally { gate.Release(); }
    }

    private async Task<Document> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new(null, []);
        string json = await File.ReadAllTextAsync(path, ct);
        var document = JsonSerializer.Deserialize<Document>(json, Options);
        return document?.Annotations is not null ? document : throw new IOException("标注文件内容无效");
    }

    private async Task WriteAsync(Document document, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, Options), new UTF8Encoding(false), ct);
        File.Move(temp, path, true);
    }
}
