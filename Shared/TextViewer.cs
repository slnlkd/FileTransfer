using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;
public sealed class TextViewer : Window
{
    public TextBox Editor { get; } = new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = new("Consolas"), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Button save = new() { Content = "保存到服务端（Ctrl+S）", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(5) };
    private readonly RemoteFileClient client;
    private readonly string path;
    private string? version;
    private readonly string? encoding;
    private bool dirty;
    private bool saving;
    public TextViewer(RemoteFileClient client, string path, RemoteResponse response)
    {
        this.client = client; this.path = path; version = response.Version; encoding = response.EncodingName;
        Title = "文本 · " + path; Width = 1000; Height = 720;
        var panel = new DockPanel();
        DockPanel.SetDock(save, Dock.Top); panel.Children.Add(save);
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        panel.Children.Add(Editor); Content = panel;
        Editor.Text = response.Text ?? "";
        status.Text = $"编码：{encoding}；保存前会检查远程版本，内容变更将拒绝覆盖。";
        Editor.TextChanged += (_, _) => { dirty = true; Title = "* 文本 · " + path; };
        save.Click += async (_, _) => await SaveAsync();
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; await SaveAsync(); } };
        Closing += (_, e) =>
        {
            if (saving) { e.Cancel = true; status.Text = "正在保存，请稍候再关闭。"; return; }
            if (dirty && MessageBox.Show(this, "有未保存的文本修改，是否放弃并关闭？", "未保存内容", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }
    public async Task SaveAsync()
    {
        if (saving) return;
        saving = true; save.IsEnabled = false; Editor.IsReadOnly = true;
        try
        {
            var response = await client.CallAsync(new("save", path, Editor.Text, version, encoding));
            version = response.Version; dirty = false; Title = "文本 · " + path;
            status.Text = "已保存到服务端：" + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) { Log.Error(ex, "文本窗口保存失败；路径 {RemotePath}", path); status.Text = "保存失败，编辑内容仍保留：" + ex.Message; }
        finally { saving = false; save.IsEnabled = true; Editor.IsReadOnly = false; }
    }
}
