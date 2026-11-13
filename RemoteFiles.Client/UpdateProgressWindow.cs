using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteFiles.Desktop;

namespace RemoteFiles.Client;

internal sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock status = new()
    {
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0x63, 0x68)),
        Margin = new Thickness(0, 0, 0, 10)
    };
    private readonly ProgressBar progress = new()
    {
        Height = 6,
        Maximum = 100
    };

    public UpdateProgressWindow()
    {
        ThemeLoader.Apply(this);
        Title = "正在更新客户端";
        Width = 460;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "正在更新客户端", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) },
                status,
                progress
            }
        };
    }

    public void Report(string text, double percent)
    {
        status.Text = text;
        progress.Value = Math.Clamp(percent, 0, 100);
    }
}
