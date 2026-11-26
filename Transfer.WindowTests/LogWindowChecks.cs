extern alias RemoteServer;
extern alias RemoteClient;
using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Transfer.Core.Remote;
using ClientWindow = RemoteClient::RemoteFiles.Client.MainWindow;
using ServerWindow = RemoteServer::RemoteFiles.Server.MainWindow;
using ClientBrowser = RemoteClient::RemoteFiles.Desktop.RemoteBrowser;
using TextViewer = RemoteClient::RemoteFiles.Desktop.TextViewer;
using ImageViewer = RemoteClient::RemoteFiles.Desktop.ImageViewer;
using AnnotationContext = RemoteClient::RemoteFiles.Desktop.AnnotationContext;
using ImageThumbItem = RemoteClient::RemoteFiles.Desktop.ImageThumbItem;
using ProcessViewer = RemoteClient::RemoteFiles.Desktop.ProcessViewer;

internal static partial class RemoteWindowChecks
{
    public static async Task CheckLogDirectoryOpeningAsync(string temp)
    {
        string folder = Path.Combine(temp, "中文 日志查看");
        Directory.CreateDirectory(folder);
        string logFile = Path.Combine(folder, "app-test.log");
        await File.WriteAllTextAsync(logFile, "HEAD_SENTINEL\n" + string.Concat(Enumerable.Repeat(new string('x', 210) + "\n", 4000)) + "TAIL_SENTINEL\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "error-test.log"), "错误日志示例");
        File.SetLastWriteTimeUtc(logFile, DateTime.UtcNow.AddMinutes(1));
        string original = Transfer.Core.AppLogging.LogDirectory;
        var client = new ClientWindow(Path.Combine(temp, "log-viewer-client.json"));
        Prepare(client); client.Show();
        try
        {
            typeof(Transfer.Core.AppLogging).GetProperty("LogDirectory")!.SetValue(null, folder);
            var logsButton = FindButton(client, "查看日志") ?? throw new Exception("缺少日志按钮");
            logsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var viewer = client.OwnedWindows.OfType<Window>().Single(w => w.Title == "客户端日志");
            var editor = FindDescendant<TextBox>(viewer)!;
            await WaitAsync(() => editor.Text.Contains("TAIL_SENTINEL"));
            if (!editor.IsReadOnly || editor.Text.Contains("HEAD_SENTINEL") || editor.Text.Split('\n').Length > 2001)
                throw new Exception("日志查看应只读并限制大日志内容");
            logsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (client.OwnedWindows.OfType<Window>().Count(w => w.Title == "客户端日志") != 1)
                throw new Exception("重复点击日志应复用已有窗口");
            using (var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream)) await writer.WriteLineAsync("REFRESH_SENTINEL");
            FindButtonByContent(viewer, "刷新")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => editor.Text.Contains("REFRESH_SENTINEL"));
            var files = FindDescendant<ListBox>(viewer)!;
            files.SelectedIndex = 1;
            await WaitAsync(() => editor.Text == "错误日志示例");
            FindButtonByContent(viewer, "复制路径")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (Clipboard.GetText() != Path.Combine(folder, "error-test.log")) throw new Exception("复制日志路径错误");
            if (Environment.GetEnvironmentVariable("FILETRANSFER_LOG_VIEWER_PREVIEW") is { Length: > 0 } preview)
            {
                await FlushAsync(viewer);
                var rendered = new RenderTargetBitmap((int)Math.Ceiling(viewer.ActualWidth), (int)Math.Ceiling(viewer.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rendered.Render(viewer);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                using var output = File.Create(preview); encoder.Save(output);
            }
            viewer.Close();
            var empty = new RemoteClient::RemoteFiles.Client.LogViewer(Path.Combine(temp, "empty-logs"));
            Prepare(empty); empty.Show(); await FlushAsync(empty);
            if (FindDescendant<ListBox>(empty)!.Items.Count != 0 || !FindTextBlocks(empty).Any(t => t.Text == "暂无日志文件"))
                throw new Exception("空日志目录应显示明确的空状态");
            empty.Close();
            Console.WriteLine("通过：日志按钮打开内置窗口、复用、文件切换、只读大日志截断、并发追加后刷新及复制路径");
        }
        finally
        {
            typeof(Transfer.Core.AppLogging).GetProperty("LogDirectory")!.SetValue(null, original);
            await CloseAsync(client);
        }
    }

}
