using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RemoteFiles.Desktop;

/// <summary>对话框语气：只影响按钮样式和默认焦点，不改变文案。</summary>
public enum DialogTone { Info, Question, Warning, Error }

/// <summary>
/// 主题化确认框。原生 MessageBox 不跟随 WPF 主题，夜间模式下是一块白底，
/// 所以需要用户确认或提示的地方统一走这里：背景、文字和按钮都使用主题资源，
/// 标题栏由 ThemeManager 在窗口 Loaded 时同步成深色。
/// </summary>
public static class Dialog
{
    /// <summary>是/否确认，默认焦点在“确定”。</summary>
    public static bool Confirm(Window? owner, string message, string title = "请确认")
        => Show(owner, title, message, DialogTone.Question, "确定", "取消", danger: false) == true;

    /// <summary>危险操作确认：按钮为告警样式，文案默认“删除”。</summary>
    public static bool ConfirmDanger(Window? owner, string message, string title = "请确认")
        => Show(owner, title, message, DialogTone.Warning, "删除", "取消", danger: true) == true;

    /// <summary>只有“确定”的提示框。</summary>
    public static void Notify(Window? owner, string message, string title = "提示", DialogTone tone = DialogTone.Info)
        => Show(owner, title, message, tone, "确定", null, danger: tone == DialogTone.Error);

    internal static MessageBoxResult ConfirmSaveChanges(Window owner, string message)
    {
        bool discarded = false;
        bool? saved = Show(owner, "未保存内容", message, DialogTone.Question, "保存", "取消",
            danger: false, discard: () => discarded = true);
        return saved == true ? MessageBoxResult.Yes : discarded ? MessageBoxResult.No : MessageBoxResult.Cancel;
    }

    private static bool? Show(Window? owner, string title, string message, DialogTone tone, string acceptText, string? cancelText,
        bool danger, Action? discard = null)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null) window.Owner = owner;

        var panel = new StackPanel();
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        panel.Children.Add(heading);

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 10, 0, 0)
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        panel.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var accept = new Button
        {
            Content = acceptText,
            MinWidth = 76,
            IsDefault = true,
            Style = StyleFor(danger ? "FormDangerButton" : "FormPrimaryButton")
        };
        accept.Click += (_, _) => window.DialogResult = true;
        if (discard is not null)
        {
            accept.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(accept);
            var discardButton = new Button { Content = "放弃", MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), Style = StyleFor("FormButton") };
            discardButton.Click += (_, _) => { discard(); window.DialogResult = false; };
            buttons.Children.Add(discardButton);
        }
        if (cancelText is not null)
        {
            // IsCancel 自带 Esc 与“返回 false”，不要再挂 Click 重复设置 DialogResult。
            buttons.Children.Add(new Button
            {
                Content = cancelText,
                Style = StyleFor("FormButton"),
                MinWidth = 76,
                IsCancel = true,
                Margin = discard is null ? new Thickness(0, 0, 8, 0) : new Thickness(0)
            });
        }
        if (discard is null) buttons.Children.Add(accept);
        panel.Children.Add(buttons);

        var root = new Border
        {
            Padding = new Thickness(20, 18, 20, 16),
            Child = panel
        };
        root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        window.Content = root;

        // 只有“确定”的提示框没有取消按钮，Esc 需要单独处理，否则只能点标题栏的关闭。
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            window.DialogResult = false;
            e.Handled = true;
        };

        return window.ShowDialog();
    }

    private static Style? StyleFor(string key) => Application.Current?.TryFindResource(key) as Style;
}
