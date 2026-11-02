using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;
public sealed class ProcessViewer : Window
{
    private readonly RemoteFileClient client;
    private readonly string path;
    private readonly TextBlock status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool busy;
    public ProcessViewer(RemoteFileClient client, string path)
    {
        this.client = client; this.path = path; Title = "程序运行状态 · " + path; Width = 740; Height = 300;
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(status); Content = panel;
        foreach (var action in new[] { ("刷新状态", "status"), ("停止程序", "stop"), ("启动程序", "start"), ("重新启动", "restart") })
        {
            var button = new Button { Content = action.Item1, Margin = new Thickness(4), Padding = new Thickness(12, 7, 12, 7) };
            button.Click += async (_, _) => await ExecuteAsync(action.Item2); buttons.Children.Add(button);
        }
        Loaded += async (_, _) => { await ExecuteAsync("status"); timer.Start(); };
        timer.Tick += async (_, _) => await ExecuteAsync("status");
        Closed += (_, _) => timer.Stop();
    }
    public async Task ExecuteAsync(string command)
    {
        if (busy) return;
        busy = true; buttons.IsEnabled = false;
        try
        {
            if (command == "restart") await client.CallAsync(new("stop", path));
            var response = await client.CallAsync(new(command == "restart" ? "start" : command, path));
            var state = response.Process;
            status.Text = $"文件：{path}\n状态：{state?.State}\nPID：{string.Join(", ", state?.ProcessIds ?? [])}\n{state?.Detail}\n检查时间：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { Log.Error(ex, "程序管理窗口操作失败；命令 {Command}，路径 {RemotePath}", command, path); status.Text = ex.Message; }
        finally { busy = false; buttons.IsEnabled = true; }
    }
}
