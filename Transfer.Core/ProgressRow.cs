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
    public void Update(TransferProgress p)
    {
        Percent = p.Total == 0 ? (p.State == "已完成" ? 100 : 0) : 100d * p.Completed / p.Total;
        Status = p.State;
        Detail = $"{p.Completed / 1048576d:N2} / {p.Total / 1048576d:N2} MB · {p.Session}";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
