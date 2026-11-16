using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Serilog;
using Transfer.Core;

namespace Transfer.Desktop;

/// <summary>
/// 全局日间/夜间主题。主题资源由 App.xaml 的 Theme/Tokens.xaml 提供，
/// 切换时替换该字典的 Source，所有 DynamicResource 引用会立即刷新。
/// </summary>
public static class ThemeManager
{
    private sealed record ThemeSettings(bool Dark);
    private static bool initialized;
    private static readonly bool AnimationsEnabled =
        Environment.GetEnvironmentVariable("FILETRANSFER_DISABLE_THEME_ANIMATION") != "1";

    public static bool IsDark
    {
        // 用 Application.Properties 存状态：共享主题资源是全局的，
        // 而 ThemeManager 会被链接进多个程序集，静态字段在各程序集里并不共享。
        get => Application.Current?.Properties[ThemeStateKey] is true;
        private set { if (Application.Current is { } app) app.Properties[ThemeStateKey] = value; }
    }
    private const string ThemeStateKey = "FileTransfer.ThemeDark";
    public static event Action? Changed;

    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        bool dark = false;
        try
        {
            string path = AppLogging.GetSettingsPath("theme.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(path)) is { } saved)
                dark = saved.Dark;
        }
        catch (Exception ex) { Log.Warning(ex, "读取主题设置失败，使用日间模式"); }
        Apply(dark, false, false);
        // 之后打开的窗口（查看器、进度窗口等）在加载时同步标题栏配色。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) ApplyTitleBar(window, IsDark); }));
    }

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark, bool persist = true, bool animate = true)
    {
        var app = Application.Current;
        if (app is null) return;
        if (FindTokens(app.Resources) is { } tokens)
        {
            string assembly = app.GetType().Assembly.GetName().Name!;
            string file = dark ? "TokensDark.xaml" : "Tokens.xaml";
            var uri = new Uri($"pack://application:,,,/{assembly};component/Theme/{file}", UriKind.Absolute);
            // 标题栏由 Windows 渐变切换，内容也做相同时长的过渡，避免一半渐变一半瞬切。
            if (animate && AnimationsEnabled) AnimateTokens(tokens, uri);
            else tokens.Source = uri;
        }
        IsDark = dark;
        if (persist) Save(dark);
        foreach (Window window in app.Windows) ApplyTitleBar(window, dark);
        Changed?.Invoke();
    }

    private static void AnimateTokens(ResourceDictionary tokens, Uri targetUri)
    {
        var target = new ResourceDictionary { Source = targetUri };
        foreach (var key in target.Keys)
        {
            if (target[key] is not SolidColorBrush targetBrush || tokens[key] is not SolidColorBrush source) continue;
            // 资源字典里的画刷可能已被冻结；先复制并在加入字典前启动动画，
            // 否则资源字典会把画刷冻结，导致无法播放颜色过渡。
            var brush = source.IsFrozen ? source.Clone() : source;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            Color from = brush.Color;
            brush.Color = targetBrush.Color;
            if (from != targetBrush.Color)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(from, targetBrush.Color, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                        FillBehavior = FillBehavior.Stop
                    });
            }
            if (!ReferenceEquals(brush, source)) tokens[key] = brush;
        }
        // 阴影等非画刷资源不参与颜色动画，直接替换。
        foreach (var key in target.Keys)
            if (target[key] is not SolidColorBrush) tokens[key] = target[key];
    }

    // Windows 10 1809+ 支持深色标题栏；旧系统调用失败时保持系统默认。
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeLegacy, ref value, sizeof(int));
        }
        catch (Exception ex) { Log.Debug(ex, "设置标题栏夜间配色失败"); }
    }

    private static void Save(bool dark)
    {
        try
        {
            string path = AppLogging.GetSettingsPath("theme.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new ThemeSettings(dark)), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warning(ex, "保存主题设置失败"); }
    }

    private static ResourceDictionary? FindTokens(ResourceDictionary root)
    {
        foreach (var dictionary in root.MergedDictionaries)
        {
            string? source = dictionary.Source?.OriginalString;
            if (source is not null && (source.EndsWith("Tokens.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("TokensDark.xaml", StringComparison.OrdinalIgnoreCase)))
                return dictionary;
            if (FindTokens(dictionary) is { } nested) return nested;
        }
        return null;
    }
}
