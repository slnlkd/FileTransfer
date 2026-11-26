using System.Windows;
using Serilog;

namespace RemoteFiles.Desktop;

/// <summary>管理查看窗口的所有权、标注窗口唯一性和关闭后的异步协调。</summary>
public sealed class ViewerCoordinator
{
    public ImageViewer? AnnotationWindow { get; private set; }
    public event Action? AnnotationWindowChanged;

    public void Show(Window viewer, Window? owner) { viewer.Owner = owner; viewer.Show(); }

    public void ShowImage(ImageViewer viewer, Window? owner, Func<string, Task> restore)
    {
        viewer.Closed += async (_, _) =>
        {
            try { await restore(viewer.CurrentPath); }
            catch (Exception ex) { Log.Debug(ex, "关闭查看器后定位图片失败；路径 {RemotePath}", viewer.CurrentPath); }
        };
        Show(viewer, owner);
    }

    public void ShowAnnotation(ImageViewer viewer, Window? owner, Func<bool> canActivate, Func<Task> refresh, Func<Task> focusNext)
    {
        if (AnnotationWindow is not null) throw new InvalidOperationException("标注窗口已打开");
        viewer.Closed += async (_, _) =>
        {
            try
            {
                if (canActivate() && owner is { IsVisible: true })
                {
                    if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                    owner.Activate();
                }
                await refresh();
            }
            catch (Exception ex) { Log.Debug(ex, "标注窗口关闭后刷新状态失败"); }
            finally { AnnotationWindow = null; AnnotationWindowChanged?.Invoke(); }
            if (viewer.AnnotationSubmitted)
            {
                try { await focusNext(); }
                catch (Exception ex) { Log.Debug(ex, "定位下一未标注批次失败"); }
            }
        };
        AnnotationWindow = viewer;
        try { Show(viewer, owner); }
        catch { AnnotationWindow = null; throw; }
    }
}
