using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using Transfer.Core;

namespace Transfer.Desktop;

/// <summary>保存窗口大小、位置和列表列宽；测试环境通过环境变量禁用。</summary>
public static class WindowLayout
{
    private sealed record ColumnState(string Mode, double Value);
    private sealed record State(double Left, double Top, double Width, double Height, bool Maximized, Dictionary<string, ColumnState> Columns);

    public static void Attach(Window window, string key, params DataGrid[] grids)
    {
        if (Environment.GetEnvironmentVariable("FILETRANSFER_DISABLE_LAYOUT") == "1") return;
        string path = AppLogging.GetSettingsPath($"layout-{key}.json");
        window.Loaded += (_, _) => Load(window, path, grids);
        window.Closing += (_, _) => Save(window, path, grids);
    }

    private static void Load(Window window, string path, DataGrid[] grids)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (JsonSerializer.Deserialize<State>(File.ReadAllText(path)) is not { } state) return;
            if (state.Width >= 300 && state.Height >= 200)
            {
                double left = Math.Clamp(state.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120);
                double top = Math.Clamp(state.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = left; window.Top = top;
                window.Width = state.Width; window.Height = state.Height;
            }
            if (state.Maximized) window.WindowState = WindowState.Maximized;
            for (int i = 0; i < grids.Length; i++)
            {
                var grid = grids[i];
                foreach (var column in grid.Columns)
                {
                    string header = column.Header?.ToString() ?? "";
                    if (!state.Columns.TryGetValue($"{i}:{header}", out var width)) continue;
                    if (width.Mode == "Pixel" && width.Value < 5) continue;
                    column.Width = width.Mode switch
                    {
                        "Star" => new DataGridLength(width.Value, DataGridLengthUnitType.Star),
                        "Auto" => DataGridLength.Auto,
                        _ => new DataGridLength(width.Value)
                    };
                }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "恢复窗口布局失败；路径 {LayoutPath}", path); }
    }

    private static void Save(Window window, string path, DataGrid[] grids)
    {
        try
        {
            Rect bounds = window.WindowState == WindowState.Maximized ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);
            var columns = new Dictionary<string, ColumnState>();
            for (int i = 0; i < grids.Length; i++)
            {
                foreach (var column in grids[i].Columns)
                {
                    string header = column.Header?.ToString() ?? "";
                    if (!column.Width.IsStar && !column.Width.IsAuto && column.ActualWidth < 5) continue;
                    var state = column.Width.IsStar
                        ? new ColumnState("Star", column.Width.Value)
                        : column.Width.IsAuto ? new ColumnState("Auto", 0) : new ColumnState("Pixel", column.ActualWidth);
                    columns[$"{i}:{header}"] = state;
                }
            }
            var layout = new State(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == WindowState.Maximized, columns);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(layout));
        }
        catch (Exception ex) { Log.Warning(ex, "保存窗口布局失败；路径 {LayoutPath}", path); }
    }
}
