using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace Transfer.Core;
public sealed class ProgressRow : INotifyPropertyChanged
{
    public required SharedFile File { get; init; }
    public string SourcePath { get; init; } = "";
    public string Name => File.RelativePath ?? File.Name;
    public string Size => File.IsDirectory ? "文件夹" : $"{File.Length / 1048576d:N2} MB";
    private double percent;
    public double Percent { get => percent; set { percent = value; Changed(); } }
    private string status = "等待下载";
    public string Status { get => status; set { status = value; Changed(); } }
    private string detail = "";
    public string Detail { get => detail; set { detail = value; Changed(); } }
    private string speed = "";
    public string Speed { get => speed; private set { speed = value; Changed(); } }
    private string remaining = "";
    public string Remaining { get => remaining; private set { remaining = value; Changed(); } }
    private DateTime lastSampleUtc = DateTime.UtcNow;
    private long lastCompleted;
    private double speedBytes;
    public void Update(TransferProgress p)
    {
        Percent = p.Total == 0 ? (p.State == "已完成" ? 100 : 0) : 100d * p.Completed / p.Total;
        Status = p.State;
        Detail = $"{p.Completed / 1048576d:N2} / {p.Total / 1048576d:N2} MB · {p.Session}";
        var now = DateTime.UtcNow;
        long delta = p.Completed - lastCompleted;
        if (delta < 0)
        {
            // 新会话或断点重连，重新采样。
            lastCompleted = p.Completed;
            lastSampleUtc = now;
            speedBytes = 0;
        }
        else if (p.State == "下载中")
        {
            double elapsed = (now - lastSampleUtc).TotalSeconds;
            if (elapsed >= 0.4)
            {
                speedBytes = Math.Max(0, delta / elapsed);
                lastSampleUtc = now;
                lastCompleted = p.Completed;
            }
        }
        else speedBytes = 0;
        Speed = speedBytes > 0 ? FormatRate(speedBytes) : "";
        if (p.State.Contains("完成"))
            Remaining = "";
        else if (speedBytes > 0 && p.Total > p.Completed)
            Remaining = FormatDuration((p.Total - p.Completed) / speedBytes);
        else Remaining = "";
    }
    private static string FormatRate(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:F0} B/s",
        < 1024 * 1024 => $"{bytes / 1024:F1} KB/s",
        _ => $"{bytes / (1024 * 1024):F1} MB/s"
    };
    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(0, seconds):F0} 秒";
        if (seconds < 3600) return $"{(int)(seconds / 60)} 分 {(int)(seconds % 60)} 秒";
        return $"{(int)(seconds / 3600)} 小时 {(int)(seconds % 3600 / 60)} 分";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
