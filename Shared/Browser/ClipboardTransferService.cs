using System.IO;
using System.IO.Compression;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>准备剪贴板文件并管理当前进程的临时会话；剪贴板写入由界面负责。</summary>
public sealed class ClipboardTransferService
{
    private static readonly string ClipboardTempRoot = Path.Combine(AppContext.BaseDirectory, "Clipboard", Environment.UserName, Environment.ProcessId.ToString());
    private static readonly List<string> clipboardSessions = [];
    public async Task<string[]> PrepareAsync(RemoteFileClient endpoint, IReadOnlyList<RemoteEntry> selected, Action<double, string> report, CancellationToken ct)
    {
        if (selected.Count == 0) return [];
        string tempRoot = CreateClipboardSession();
        clipboardSessions.Add(tempRoot);
        int completedItems = 0;
        var progress = new Progress<RemoteTransferProgress>(value =>
        {
            double fraction = CopyStageFraction(value.Stage, value.Percent);
            double overall = (completedItems + fraction) * 100d / selected.Count;
            report(overall, $"{value.Stage} {overall:F0}% · {completedItems + 1}/{selected.Count}：{Path.GetFileName(value.Path.TrimEnd('/'))}");
        });
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            string archive = await endpoint.CompressAsync(item.Path, progress, ct);
            string zipLocal = Path.Combine(tempRoot, "__copy-" + Guid.NewGuid().ToString("N") + ".zip");
            await endpoint.DownloadZipAsync(archive, zipLocal, progress, ct);
            ct.ThrowIfCancellationRequested();
            try { ZipFile.ExtractToDirectory(zipLocal, tempRoot, true); }
            catch (Exception ex) { throw new IOException("解压复制内容失败：" + ex.Message, ex); }
            File.Delete(zipLocal);
            await DeleteServerArchiveAsync(endpoint, archive, ct);
            completedItems++;
            report(completedItems * 100d / selected.Count, $"已完成 {completedItems}/{selected.Count}（{completedItems * 100d / selected.Count:F0}%）");
        }
        string[] paths = Directory.GetFileSystemEntries(tempRoot);
        if (paths.Length == 0) throw new IOException("复制内容为空");
        return paths;
    }
    // 单个条目的压缩/下载阶段折算成整体进度，避免压缩完成后进度回退。
    private static double CopyStageFraction(string? stage, double percent)
    {
        double p = Math.Clamp(percent, 0, 100) / 100d;
        return stage switch
        {
            "扫描目录" => 0,
            "压缩" => 0.35 * p,
            "计算校验值" or "准备下载" or "接收数据" => 0.35,
            "下载" => 0.35 + 0.63 * p,
            "下载完成" => 0.99,
            _ => 0.35 * p
        };
    }
    private static string CreateClipboardSession()
    {
        CleanClipboardTemp();
        CleanStaleClipboardRoots();
        string session = Path.Combine(ClipboardTempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        return session;
    }
    private static void CleanClipboardTemp()
    {
        foreach (string session in clipboardSessions.ToArray())
        {
            try { DeleteDirectory(session); }
            catch (Exception ex) { Log.Debug(ex, "清理剪贴板会话失败；路径 {Path}", session); }
            clipboardSessions.Remove(session);
        }
        try
        {
            if (!Directory.Exists(ClipboardTempRoot)) return;
            foreach (string directory in Directory.GetDirectories(ClipboardTempRoot))
            {
                if (clipboardSessions.Contains(directory)) continue;
                try { DeleteDirectory(directory); }
                catch (Exception ex) { Log.Debug(ex, "清理旧剪贴板临时目录失败；路径 {Path}", directory); }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "扫描剪贴板临时目录失败"); }
    }
    private static void CleanStaleClipboardRoots()
    {
        try
        {
            string userRoot = Path.GetDirectoryName(ClipboardTempRoot)!;
            if (!Directory.Exists(userRoot)) return;
            foreach (string directory in Directory.GetDirectories(userRoot))
            {
                if (string.Equals(directory, ClipboardTempRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // 只清理长期未使用的其他进程目录，避免误删正在粘贴的数据。
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(7)) continue;
                    DeleteDirectory(directory);
                }
                catch (Exception ex) { Log.Debug(ex, "清理过期剪贴板目录失败；路径 {Path}", directory); }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "扫描过期剪贴板目录失败"); }
    }
    private static void DeleteDirectory(string path)
    {        if (!Directory.Exists(path)) return;
        // 解压出来的文件可能带只读属性，先清除再删除。
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (Exception ex) { Log.Debug(ex, "清除只读属性失败；路径 {Path}", file); }
        }
        Directory.Delete(path, true);
    }
    private static async Task DeleteServerArchiveAsync(RemoteFileClient endpoint, string archive, CancellationToken ct)
    {
        try
        {
            int slash = archive.LastIndexOf('/');
            string parent = slash < 0 ? "" : archive[..slash];
            var listing = await endpoint.CallAsync(new("list", parent), ct);
            var zip = (listing.Entries ?? []).FirstOrDefault(e => !e.IsDirectory && e.Path == archive);
            if (zip is not null) await endpoint.DeleteFileAsync(zip, ct);
        }
        catch (Exception ex) { Log.Warning(ex, "清理复制用的服务端 ZIP 失败；路径 {ArchivePath}", archive); }
    }
}
