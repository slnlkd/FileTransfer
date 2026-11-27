using System.Runtime.InteropServices;
using System.Windows;
using RemoteFiles.Desktop;
using Transfer.Core.Remote;
using Transfer.Desktop;

namespace RemoteFiles.Client;

public partial class AnnotationDatabaseWindow : Window
{
    private readonly AnnotationDatabaseViewModel model;

    public AnnotationDatabaseWindow(RemoteFileClient client)
    {
        model = new(client.GetAnnotationDatabaseInfoAsync);
        InitializeComponent();
        ThemeLoader.Apply(this);
        ThemeManager.SetMatchCaptionToCanvas(this, true);
        Title = $"标注数据库诊断 · 服务端 {EndpointSettingsService.FormatEndpoint(client.Host, client.Port)}";
        DataContext = model;
        Loaded += LoadOnce;
        Closed += (_, _) => model.Dispose();
    }

    private async void LoadOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadOnce;
        await model.LoadAsync(verify: false);
    }

    private async void Verify_Click(object sender, RoutedEventArgs e) => await model.LoadAsync(verify: true);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SqlText.Sql)) return;
        try { Clipboard.SetText(SqlText.Sql); }
        catch (ExternalException) { MessageBox.Show(this, "剪贴板正在使用，请稍后重试。", "复制 SQL"); }
    }
}
