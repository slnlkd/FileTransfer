using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Transfer.Core.Remote;

/// <summary>标注仓储：只负责存取，校验、路径解析在 <see cref="AnnotationStore"/>。</summary>
public interface IAnnotationRepository
{
    // 按标注时间从新到旧返回；batch 为空时返回整天的记录。
    Task<RemoteAnnotation[]> ListAsync(string equipmentNo, DateTime date, CancellationToken ct = default, string? batch = null);
    Task<RemoteAnnotation> SaveAsync(RemoteAnnotation annotation, CancellationToken ct = default);
    Task<int> DeleteImageAsync(string equipmentNo, DateTime date, string batch, string folderPath, string imageName, CancellationToken ct = default);
    Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct = default);
    Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct = default);
}

/// <summary>
/// 标注读写入口。每次提交追加一张图片的标注记录：
/// 路径形如 机台/yyyyMMdd/批次，机台、日期、批次从路径解析后落到
/// AutoLineFailureAnalysisLog 的 EQP_ID / AUTOLINE_DATE / JOB_BATCH_NUMBER。
/// </summary>
public sealed class AnnotationStore(RemoteFileStore store, IAnnotationRepository repository)
{
    public const int NoteLimit = 500;
    public const int OperatorLimit = 100;
    public const int NameLimit = 200;
    private readonly SemaphoreSlim gate = new(1, 1);
    private RemoteDictionary? dictionary;
    private DateTime dictionaryAt;

    public async Task<RemoteDictionary> DictionaryAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (dictionary is { } fresh && DateTime.UtcNow - dictionaryAt < TimeSpan.FromMinutes(10)) return fresh;
            dictionary = await repository.GetDictionaryAsync(ct);
            dictionaryAt = DateTime.UtcNow;
            return dictionary;
        }
        finally { gate.Release(); }
    }

    public async Task<RemoteDictionary> AddDictionaryItemAsync(string group, string? text, CancellationToken ct = default)
    {
        var kind = group switch
        {
            "category" => AnnotationDictionaryKind.Category,
            "plan" => AnnotationDictionaryKind.Plan,
            _ => throw new IOException("不支持的标注字典分组")
        };
        string name = (text ?? "").Trim();
        if (name.Length == 0) throw new IOException("请填写字典项名称");
        if (name.Length > NameLimit) throw new IOException($"字典项名称不能超过 {NameLimit} 个字符");
        if (name.Any(char.IsControl)) throw new IOException("字典项名称不能包含控制字符");

        await gate.WaitAsync(ct);
        try
        {
            dictionary = null;
            dictionary = await repository.AddDictionaryItemAsync(kind, name, ct);
            dictionaryAt = DateTime.UtcNow;
            return dictionary;
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

    internal static bool SameFolder(string? storedPath, string folderPath) =>
        string.Equals(storedPath?.Replace('\\', '/').TrimEnd('/'), folderPath, StringComparison.OrdinalIgnoreCase);

    private sealed record FolderKey(string Normalized, string Equipment, DateTime Date, string Batch, string? EquipmentType);

    /// <summary>解析 机台/yyyyMMdd/批次；允许中间存在额外层级。</summary>
    private FolderKey ParseFolder(string relative)
    {
        string full = store.Resolve(relative);
        if (!Directory.Exists(full)) throw new IOException("对线文件夹不存在：" + relative);
        string normalized = Relative(full);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) throw new IOException("路径不符合 机台/日期/批次 结构：" + normalized);
        for (int i = 1; i < segments.Length - 1; i++)
        {
            if (!DateTime.TryParseExact(segments[i], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            return new(normalized, segments[i - 1], DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), segments[^1], null);
        }
        throw new IOException("路径中没有 yyyyMMdd 日期目录：" + normalized);
    }

    /// <summary>解析 机台/yyyyMMdd，用于按天查询标注状态。</summary>
    private FolderKey ParseDateDirectory(string relative)
    {
        string full = store.Resolve(relative);
        if (!Directory.Exists(full)) throw new IOException("目录不存在：" + relative);
        string normalized = Relative(full);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) throw new IOException("路径不符合 机台/日期 结构：" + normalized);
        for (int i = 1; i < segments.Length; i++)
        {
            if (!DateTime.TryParseExact(segments[i], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            return new(normalized, segments[i - 1], DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), "", null);
        }
        throw new IOException("路径中没有 yyyyMMdd 日期目录：" + normalized);
    }

    private string Relative(string full) => Path.GetRelativePath(store.Root, full).Replace('\\', '/');
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
                && (batch is null || string.Equals(item.BatchNumber, batch, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
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
            bool category = kind == AnnotationDictionaryKind.Category;
            var items = category ? dictionary.Categories : dictionary.Plans;
            if (items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) return dictionary;
            RemoteDictionaryItem[] updated = [.. items, new(name, name)];
            dictionary = category ? dictionary with { Categories = updated } : dictionary with { Plans = updated };
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
