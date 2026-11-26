using System.IO;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>远程上传与归档操作；界面通过回调提供覆盖决策和进度。</summary>
public sealed class RemoteFileOperationService
{
    public async Task<int> UploadFilesAsync(RemoteFileClient endpoint, IEnumerable<string> files, string targetDirectory,
        Func<string, bool> exists, Func<string, bool> confirmOverwrite, Action<string, double?> report, CancellationToken ct)
    {
        int uploaded = 0;
        foreach (string local in files)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(local);
            bool overwrite = exists(name);
            if (overwrite && !confirmOverwrite(name)) continue;
            report(name, null);
            var progress = new Progress<double>(value => report(name, value));
            await endpoint.UploadAsync(local, JoinRemote(targetDirectory, name), overwrite, progress, ct);
            uploaded++;
        }
        return uploaded;
    }
    public async Task UploadPathsAsync(RemoteFileClient endpoint, IReadOnlyList<string> paths, string targetDirectory,
        Func<string, bool> confirmOverwrite, Action<string> onFile, Action<double, string> report, CancellationToken ct)
    {
        long total = paths.Sum(PathSize);
        long completedBytes = 0;
        var remoteEntries = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        async Task<Dictionary<string, bool>> NamesAsync(string remoteDir)
        {
            if (remoteEntries.TryGetValue(remoteDir, out var cached)) return cached;
            var entries = (await endpoint.CallAsync(new("list", remoteDir), ct)).Entries ?? [];
            var names = entries.ToDictionary(e => e.Name, e => e.IsDirectory, StringComparer.OrdinalIgnoreCase);
            remoteEntries[remoteDir] = names;
            return names;
        }
        async Task UploadPathAsync(string local, string remoteDir)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(local))
            {
                string name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string target = JoinRemote(remoteDir, name);
                var names = await NamesAsync(remoteDir);
                if (names.TryGetValue(name, out bool isDirectory))
                {
                    if (!isDirectory) throw new IOException("同名文件已存在，无法创建文件夹：" + name);
                }
                else
                {
                    await endpoint.CallAsync(new("mkdir", target), ct);
                    names[name] = true;
                }
                foreach (string child in Directory.EnumerateFileSystemEntries(local)) await UploadPathAsync(child, target);
                return;
            }
            string fileName = Path.GetFileName(local);
            var listing = await NamesAsync(remoteDir);
            bool exists = listing.TryGetValue(fileName, out bool targetIsDirectory);
            if (exists && targetIsDirectory) throw new IOException("同名文件夹已存在，无法上传文件：" + fileName);
            if (exists && !confirmOverwrite(fileName)) return;
            long length = new FileInfo(local).Length;
            onFile(fileName);
            var progress = new Progress<double>(value =>
            {
                double overall = total == 0 ? 100 : (completedBytes + length * value / 100d) * 100d / total;
                report(overall, $"上传 {fileName} {overall:F0}%");
            });
            await endpoint.UploadAsync(local, JoinRemote(remoteDir, fileName), exists, progress, ct);
            completedBytes += length;
            listing[fileName] = false;
        }
        foreach (string path in paths) await UploadPathAsync(path, targetDirectory);
    }
    public Task<string> CompressAsync(RemoteFileClient endpoint, RemoteEntry entry, IProgress<RemoteTransferProgress> progress, CancellationToken ct) => endpoint.CompressAsync(entry.Path, progress, ct);
    public Task DeleteAsync(RemoteFileClient endpoint, RemoteEntry entry, CancellationToken ct) => endpoint.DeleteFileAsync(entry, ct);
    public async Task DownloadAsync(RemoteFileClient endpoint, RemoteEntry entry, string destination, IProgress<RemoteTransferProgress> progress, CancellationToken ct)
    {
        if (!entry.IsDirectory && !entry.IsZip) throw new IOException("请选择文件夹或 ZIP");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("本地目标已存在，请更换保存名称");
        string archive = entry.IsDirectory ? await endpoint.CompressAsync(entry.Path, progress, ct) : entry.Path;
        await endpoint.DownloadZipAsync(archive, destination, progress, ct);
    }
    private static string JoinRemote(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;
    private static long PathSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch (Exception ex) { Log.Debug(ex, "统计拖入文件大小失败；路径 {Path}", file); }
            }
            return total;
        }
        catch (Exception ex) { Log.Debug(ex, "统计拖入路径大小失败；路径 {Path}", path); return 0; }
    }
}
