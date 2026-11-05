using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace RemoteFiles.Desktop;

public partial class RemoteBrowser
{
    // 个性化 NG 过滤入口；不在仓库中提供时界面保持通用。
    partial void BuildLocalFilters(StackPanel host)
    {
        var check = new CheckBox
        {
            Content = "只看NG",
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "只显示数据库中 NG 的截图文件夹"
        };
        check.Checked += (_, _) => SetLocalFilter("ng");
        check.Unchecked += (_, _) => SetLocalFilter(null);
        host.Children.Add(check);
        localEmptyText = path => IsNgDirectory(path) ? "当前日期无 NG 文件夹" : null;
    }

    private static bool IsNgDirectory(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!DateTime.TryParseExact(parts[^1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        return parts[..^1].Any(part => part.StartsWith("A6", StringComparison.OrdinalIgnoreCase));
    }
}
