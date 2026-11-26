extern alias RemoteServer;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Transfer.Core.Remote;
using ServerImageViewer = RemoteServer::RemoteFiles.Desktop.ImageViewer;
using ServerAnnotationPanel = RemoteServer::RemoteFiles.Desktop.AnnotationPanel;
using ServerAnnotationContext = RemoteServer::RemoteFiles.Desktop.AnnotationContext;

internal static partial class RemoteWindowChecks
{
    private static async Task CheckServerAnnotationPanelAsync(string temp)
    {
        const string folder = "A601/20261120/1";
        string root = Path.Combine(temp, "server-annotation");
        Directory.CreateDirectory(Path.Combine(root, folder));
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(root, folder, "image.png"))) encoder.Save(output);
        string repositoryPath = Path.Combine(temp, "server-annotation.json");
        await File.WriteAllTextAsync(repositoryPath, JsonSerializer.Serialize(new
        {
            dictionary = new RemoteDictionary([new("归类", "归类")], [new("方案", "方案")]),
            annotations = Array.Empty<RemoteAnnotation>()
        }));
        await using var service = new RemoteFileService(root, new JsonAnnotationRepository(repositoryPath));
        service.Start(0);
        var endpoint = new RemoteFileClient("127.0.0.1", service.Port);
        var images = (await endpoint.CallAsync(new("list", folder))).Entries!;
        var viewer = new ServerImageViewer(endpoint, images, images[0].Path, new ServerAnnotationContext(folder, folder, []));
        Prepare(viewer); viewer.Show();
        try
        {
            var panel = FindDescendant<ServerAnnotationPanel>(viewer) ?? throw new Exception("服务端缺少独立标注组件");
            await WaitAsync(() => FindImage(viewer)?.Source is not null && panel.Model.Dictionary.Categories.Length > 0);
            panel.Model.Category = "归类"; panel.Model.Plan = "方案"; panel.Model.Operator = "服务端测试员";
            panel.Model.Note = "服务端表单";
            FindButtonByContent(viewer, "提交标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible);
            var records = await endpoint.GetAnnotationHistoryAsync(folder);
            if (!viewer.AnnotationSubmitted || records is not [{ Note: "服务端表单", Operator: "服务端测试员" }])
                throw new Exception("服务端共用标注组件提交失败");
            viewer = new ServerImageViewer(endpoint, images, images[0].Path, new ServerAnnotationContext(folder, folder, records));
            Prepare(viewer); viewer.Show();
            await WaitAsync(() => FindButtonByContent(viewer, "撤销标注") is { IsVisible: true } && FindImage(viewer)?.Source is not null);
            FindButtonByContent(viewer, "撤销标注")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !viewer.IsVisible);
            if ((await endpoint.GetAnnotationHistoryAsync(folder)).Length != 0 || viewer.AnnotationSubmitted)
                throw new Exception("服务端共用标注组件撤销失败");
            Console.WriteLine("通过：服务端共用标注表单、绑定输入、远程提交后关闭和撤销全部记录");
        }
        finally
        {
            if (viewer.IsVisible) viewer.Close();
        }
    }
}
