extern alias RemoteClient;
using System.IO;
using Transfer.Core.Remote;
using AnnotationContext = RemoteClient::RemoteFiles.Desktop.AnnotationContext;
using AnnotationViewModel = RemoteClient::RemoteFiles.Desktop.AnnotationViewModel;
using IAnnotationClient = RemoteClient::RemoteFiles.Desktop.IAnnotationClient;
using AnnotationWriteKind = RemoteClient::RemoteFiles.Desktop.AnnotationWriteKind;

internal static class AnnotationStateChecks
{
    public static async Task RunAsync()
    {
        static AnnotationViewModel Model(FakeClient client) => new(client,
            new AnnotationContext("A601/20261120/1", "测试批次", [
                new("A601/20261120/1", ImageName: "old.png", CategoryName: "旧归类")
            ]), "测试员");
        static void Fill(AnnotationViewModel model)
        {
            model.Category = "图像异常"; model.Plan = "厂内处理"; model.Note = "手写内容";
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        var client = new FakeClient();
        using (var model = Model(client))
        {
            model.SelectImage("new.png");
            Check(!model.IsDirty && model.Operator == "测试员" && model.History.Count == 0, "切图应初始化表单");
            Check(!await model.SubmitAsync() && model.ErrorField == "Category" && client.Saves == 0, "空归类不能发送请求");
            model.Category = "图像异常";
            Check(!await model.SubmitAsync() && model.ErrorField == "Plan", "空方案应定位字段");
            model.Plan = "厂内处理"; model.Operator = " ";
            Check(!await model.SubmitAsync() && model.ErrorField == "Operator", "空操作人应定位字段");
            model.Operator = "测试员";
            model.Note = "手写内容";
            Check(model.AppendRemark("自定义标签") && model.Note == "手写内容；自定义标签", "备注标签应追加并保留手写内容");
            Check(!model.AppendRemark("自定义标签") && model.Note == "手写内容；自定义标签", "相同标签不能重复追加");
            model.Note = new string('字', 500);
            Check(!model.AppendRemark("超长") && model.Note.Length == 500, "超长标签不能破坏已有备注");
            model.SelectImage("old.png");
            Check(!model.IsDirty && model.History.Count == 1 && model.Note.Length == 0, "切图应更新历史并重置草稿");
            Check(await model.RevokeAsync() && client.Deletes == 1, "撤销应删除图片所有记录");
        }
        using (var model = Model(client = new FakeClient { SaveError = new IOException("写入失败") }))
        {
            model.SelectImage("new.png"); Fill(model);
            int completed = 0; model.WriteSucceeded += _ => completed++;
            Check(!await model.SubmitAsync() && model.Note == "手写内容" && model.IsDirty && !model.IsWriting
                && model.Error.Contains("写入失败") && completed == 0, "提交失败应保留输入并解除操作锁");
            client.SaveError = null;
            Check(await model.SubmitAsync() && !model.IsDirty && completed == 1, "失败重试成功才通知宿主");
            Check(client.LastSaved is { ImageName: "new.png", CategoryName: "图像异常", PlanName: "厂内处理" }, "提交应绑定当前图片与表单");
        }
        using (var model = Model(client = new FakeClient { SaveGate = new() }))
        {
            model.SelectImage("new.png"); Fill(model);
            int completed = 0; model.WriteSucceeded += _ => completed++;
            var first = model.SubmitAsync();
            Check(model.IsWriting && !await model.SubmitAsync() && client.Saves == 1, "连续点击提交只能发送一次");
            client.SaveGate.SetResult();
            Check(await first && completed == 1 && !model.IsWriting, "提交完成应只通知一次");
        }
        using (var model = Model(client = new FakeClient { AddGate = new() }))
        {
            model.SelectImage("first.png");
            var adding = model.AddDictionaryItemAsync(AnnotationDictionaryKind.Category, " 新归类 ");
            model.SelectImage("second.png");
            client.AddGate.SetResult();
            Check(await adding && model.Dictionary.Categories.Any(item => item.Name == "新归类")
                && model.Category.Length == 0 && !model.IsDirty, "旧图片新增字典可以刷新选项，但不能填入新图片");
        }
        using (var model = Model(client = new FakeClient { DictionaryGate = new() }))
        {
            model.SelectImage("new.png");
            var loading = model.LoadDictionaryAsync();
            await model.AddDictionaryItemAsync(AnnotationDictionaryKind.Remark, "新备注");
            client.DictionaryGate.SetResult();
            await loading;
            Check(model.Dictionary.Remarks.Any(item => item.Name == "新备注"), "晚到的旧字典不能覆盖新增结果");
        }
        foreach (bool loadFirst in new[] { false, true })
        {
            using var model = Model(client = new FakeClient { DictionaryGate = new(), AddGate = new(), AddError = new IOException("新增失败") });
            model.SelectImage("new.png");
            var loading = model.LoadDictionaryAsync();
            var adding = model.AddDictionaryItemAsync(AnnotationDictionaryKind.Category, "新增归类");
            if (loadFirst) { client.DictionaryGate.SetResult(); await loading; }
            client.AddGate.SetResult();
            Check(!await adding, "失败的字典新增不能报成功");
            if (!loadFirst) { client.DictionaryGate.SetResult(); await loading; }
            Check(model.Dictionary.Categories.Length > 0 && model.Dictionary.Plans.Length > 0,
                "新增失败不能丢弃有效的首次字典加载，无论返回先后顺序");
        }
        using (var model = Model(client = new FakeClient { CanonicalName = "Defect" }))
        {
            model.SelectImage("new.png");
            Check(await model.AddDictionaryItemAsync(AnnotationDictionaryKind.Category, "defect") && model.Category == "Defect",
                "新增已有字典项时应选中服务端返回的标准名称");
        }
        using (var model = Model(client = new FakeClient { AddGate = new(), AddError = new IOException("旧图片请求失败") }))
        {
            model.SelectImage("first.png");
            var adding = model.AddDictionaryItemAsync(AnnotationDictionaryKind.Plan, "方案");
            model.SelectImage("second.png"); model.Note = "新图片备注";
            client.AddGate.SetResult();
            Check(!await adding && model.Error.Length == 0 && model.Note == "新图片备注",
                "旧图片字典请求失败不能把错误写入新图片");
        }
        using (var model = Model(client = new FakeClient { SaveGate = new() }))
        {
            model.SelectImage("first.png"); Fill(model);
            int completed = 0; model.WriteSucceeded += _ => completed++;
            var pendingWrite = model.SubmitAsync();
            model.SelectImage("second.png"); model.Note = "第二张";
            client.SaveGate.SetResult();
            Check(!await pendingWrite && completed == 0 && model.Note == "第二张" && model.IsDirty,
                "过期提交返回不能清除新图片草稿或关闭宿主");
        }
        client = new FakeClient { SaveGate = new() };
        var disposed = Model(client); disposed.SelectImage("new.png"); Fill(disposed);
        int afterClose = 0; disposed.WriteSucceeded += _ => afterClose++;
        var pending = disposed.SubmitAsync(); disposed.Dispose(); client.SaveGate.SetResult();
        Check(!await pending && afterClose == 0 && client.SaveToken.IsCancellationRequested, "关闭应取消请求且不得通知已关闭宿主");
        using (var model = Model(client = new FakeClient()))
        {
            model.SelectImage("new.png");
            Check(!await model.RevokeAsync() && client.Deletes == 0, "未标注图片不能撤销");
            model.SelectImage("old.png");
            AnnotationWriteKind? kind = null; model.WriteSucceeded += value => kind = value;
            Check(await model.RevokeAsync() && client.Deletes == 1 && client.LastDeleted == "old.png"
                && kind == AnnotationWriteKind.Revoke, "撤销必须针对当前图片并通知宿主");
        }
        Console.WriteLine("通过：标注状态的校验、备注追加、失败重试、重复提交、字典并发、过期结果、取消及撤销");
    }

    private sealed class FakeClient : IAnnotationClient
    {
        public int Saves, Deletes;
        public RemoteAnnotation? LastSaved;
        public string? LastDeleted;
        public string? CanonicalName;
        public Exception? SaveError, AddError;
        public CancellationToken SaveToken;
        public TaskCompletionSource? SaveGate, AddGate, DictionaryGate;
        public RemoteDictionary Words = new([new("图像异常", "图像异常")], [new("厂内处理", "厂内处理")]);
        public async Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct)
        {
            var snapshot = Words;
            if (DictionaryGate is not null) await DictionaryGate.Task;
            return snapshot;
        }
        public async Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct)
        {
            if (AddGate is not null) await AddGate.Task;
            if (AddError is not null) throw AddError;
            var item = new RemoteDictionaryItem(CanonicalName ?? name, CanonicalName ?? name);
            Words = kind switch
            {
                AnnotationDictionaryKind.Category => Words with { Categories = [.. Words.Categories, item] },
                AnnotationDictionaryKind.Plan => Words with { Plans = [.. Words.Plans, item] },
                _ => Words with { Remarks = [.. Words.Remarks, item] }
            };
            return Words;
        }
        public Task<RemoteAnnotation[]> GetHistoryAsync(string path, CancellationToken ct) => Task.FromResult(Array.Empty<RemoteAnnotation>());
        public async Task<RemoteAnnotation> SaveAsync(RemoteAnnotation record, CancellationToken ct)
        {
            Saves++; LastSaved = record; SaveToken = ct;
            if (SaveGate is not null) await SaveGate.Task;
            if (SaveError is not null) throw SaveError;
            return record;
        }
        public Task<int> DeleteImageAsync(string folder, string image, CancellationToken ct)
        {
            Deletes++; LastDeleted = image; return Task.FromResult(2);
        }
    }
}
