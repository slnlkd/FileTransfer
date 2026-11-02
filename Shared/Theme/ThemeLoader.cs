using System.Windows;

namespace RemoteFiles.Desktop;

internal static class ThemeLoader
{
    public static void Apply(Window window)
    {
        string assembly = typeof(ThemeLoader).Assembly.GetName().Name!;
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assembly};component/Theme/Controls.xaml", UriKind.Absolute)
        });
    }
}
