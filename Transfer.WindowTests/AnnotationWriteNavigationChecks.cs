extern alias RemoteClient;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Transfer.Core.Remote;
using ClientImageViewer = RemoteClient::RemoteFiles.Desktop.ImageViewer;
using ClientAnnotationPanel = RemoteClient::RemoteFiles.Desktop.AnnotationPanel;
using ClientAnnotationContext = RemoteClient::RemoteFiles.Desktop.AnnotationContext;

internal static partial class RemoteWindowChecks
{
    private static async Task CheckAnnotationWriteNavigationAsync(string temp)
    {
        const string folder = "A601/20261120/2";
        string root = Path.Combine(temp, "write-navigation");
        Directory.CreateDirectory(Path.Combine(root, folder));
        foreach (string name in new[] { "first.png", "second.png" })
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, folder, name)); encoder.Save(output);
        }
        string repositoryPath = Path.Combine(temp, "write-navigation.json");
        await File.WriteAllTextAsync(repositoryPath, JsonSerializer.Serialize(new
        {
            dictionary = new RemoteDictionary([new("归类", "归类")], [new("方案", "方案")]),
            annotations = Array.Empty<RemoteAnnotation>()
        }));
        var inner = new JsonAnnotationRepository(repositoryPath);
        await inner.SaveAsync(new(folder, "A601", AutoLineDate: new(2026, 11, 20), BatchNumber: "2",
            CategoryName: "归类", PlanName: "方案", ImageName: "second.png"));
        var repository = new GatedAnnotationRepository(inner);
        await using var service = new RemoteFileService(root, repository);
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        var images = (await endpoint.CallAsync(new("list", folder))).Entries!;
        foreach (bool revoke in new[] { false, true })
        {
            repository.Reset();
            var history = await endpoint.GetAnnotationHistoryAsync(folder);
            var viewer = new ClientImageViewer(endpoint, images, images[0].Path, new ClientAnnotationContext(folder, folder, history));
            Prepare(viewer); viewer.Show();
            Task<bool>? writing = null;
            var panel = FindDescendant<ClientAnnotationPanel>(viewer)!;
            try
            {
                await WaitAsync(() => FindImage(viewer)?.Source is not null && panel.Model.Dictionary.Categories.Length > 0);
                panel.Model.Category = "归类"; panel.Model.Plan = "方案";
                writing = revoke ? panel.Model.RevokeAsync() : panel.Model.SubmitAsync();
                await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                panel.Model.DiscardDraft();
                var filter = FindDescendant<CheckBox>(viewer)!;
                filter.IsChecked = true;
                filter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                if (filter.IsChecked != false || viewer.CurrentPath != images[0].Path)
                    throw new Exception("写入未确认时，已标注筛选必须恢复原状态且不能切图");
                await viewer.MoveToAsync(1);
                if (viewer.CurrentPath != images[0].Path || !panel.Model.IsWriting)
                    throw new Exception("写入未确认时不能通过缩略图或方向键切换图片");
                repository.Release.TrySetResult();
                if (!await writing || viewer.IsVisible) throw new Exception("收到写入结果后应正常关闭窗口");
                int count = (await endpoint.GetAnnotationHistoryAsync(folder)).Count(item => item.ImageName == images[0].Name);
                if (count != (revoke ? 0 : 1)) throw new Exception("提交/撤销应只操作一次当前图片");
            }
            finally
            {
                repository.Release.TrySetResult();
                if (writing is not null) await writing;
                if (viewer.IsVisible) { panel.Model.DiscardDraft(); viewer.Close(); }
            }
        }
        Console.WriteLine("通过：提交和撤销等待期间禁止切图及筛选，收到结果后正常关闭且仅写入一次");
    }

    private sealed class GatedAnnotationRepository(IAnnotationRepository inner) : IAnnotationRepository
    {
        public TaskCompletionSource? DictionaryRelease { get; init; }
        public int DictionaryReads;
        public TaskCompletionSource Started { get; private set; } = new();
        public TaskCompletionSource Release { get; private set; } = new();
        public void Reset() { Started = new(); Release = new(); }
        public Task<RemoteAnnotation[]> ListAsync(string equipment, DateTime date, CancellationToken ct = default, string? batch = null) =>
            inner.ListAsync(equipment, date, ct, batch);
        public Task<RemoteBatchKey[]> ListAnnotatedBatchesAsync(string equipment, DateTime start, DateTime end, CancellationToken ct) =>
            inner.ListAnnotatedBatchesAsync(equipment, start, end, ct);
        public async Task<RemoteAnnotation> SaveAsync(RemoteAnnotation record, CancellationToken ct = default)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(ct); return await inner.SaveAsync(record, ct);
        }
        public async Task<int> DeleteImageAsync(string equipment, DateTime date, string batch, string path, string image, CancellationToken ct = default)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(ct); return await inner.DeleteImageAsync(equipment, date, batch, path, image, ct);
        }
        public async Task<RemoteDictionary> GetDictionaryAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref DictionaryReads);
            if (DictionaryRelease is { } release) await release.Task.WaitAsync(ct);
            return await inner.GetDictionaryAsync(ct);
        }
        public Task<RemoteDictionary> AddDictionaryItemAsync(AnnotationDictionaryKind kind, string name, CancellationToken ct = default) =>
            inner.AddDictionaryItemAsync(kind, name, ct);
    }
}
