using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Transfer.Core;

internal static class FolderWindowChecks
{
    public static async Task RunAsync(string temp)
    {
        string source = Path.Combine(temp, "界面共享文件夹");
        Directory.CreateDirectory(Path.Combine(source, "子目录"));
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        await File.WriteAllTextAsync(Path.Combine(source, "子目录", "示例.txt"), "文件夹下载内容");
        string config = Path.Combine(temp, "folder-server.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new[] { source }));
        var server = new Transfer.Server.MainWindow(config) { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        server.Show();
        await WaitAsync(() => server.Files.Count == 4);
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        ((TextBox)server.FindName("PortBox")).Text = port.ToString();
        ((Button)server.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var client = new Transfer.Client.MainWindow(Path.Combine(temp, "folder-client.txt")) { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        client.Show();
        ((TextBox)client.FindName("PortBox")).Text = port.ToString();
        ((Button)client.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => client.Files.Count == 4 && ((Button)client.FindName("ConnectButton")).IsEnabled);
        string output = Path.Combine(temp, "folder-ui-download");
        ((TextBox)client.FindName("FolderBox")).Text = output;
        var grid = (DataGrid)client.FindName("FileGrid");
        grid.SelectedItem = client.Files.Single(f => f.File.IsDirectory && f.File.Name == "界面共享文件夹");
        // 使用现有下载按钮的 Click 事件，覆盖文件夹展开与排队逻辑。
        var button = FindButton(client, "下载所选 / 继续") ?? throw new Exception("下载按钮不存在");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => client.Files.Where(f => !f.File.IsDirectory).All(f => f.Status == "已完成"));
        string target = Path.Combine(output, "界面共享文件夹");
        if (!Directory.Exists(Path.Combine(target, "空目录")) || await File.ReadAllTextAsync(Path.Combine(target, "子目录", "示例.txt")) != "文件夹下载内容" ||
            Directory.EnumerateFiles(output, "*.part*", SearchOption.AllDirectories).Any()) throw new Exception("选择共享文件夹下载未保留目录结构或未清理断点");
        await CloseAsync(client); await CloseAsync(server);
        Console.WriteLine("通过：旧客户端选择共享文件夹下载，保留子目录及空目录并清理临时文件");
    }
    private static Button? FindButton(DependencyObject parent, string content)
    {
        if (parent is Button button && Equals(button.Content, content)) return button;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindButton(System.Windows.Media.VisualTreeHelper.GetChild(parent, i), content) is { } child) return child;
        return null;
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private static async Task CloseAsync(Window window)
    {
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
