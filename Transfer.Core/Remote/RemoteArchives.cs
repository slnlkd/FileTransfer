using System.Diagnostics;
using System.IO.Compression;
using Serilog;

namespace Transfer.Core.Remote;

internal static class RemoteArchives
{
    private sealed record Source(string Relative, string Entry, bool Directory, long Length, DateTime Modified);

    // 先快照清单，再创建临时 ZIP，避免压缩共享根目录时把输出文件压入自身。
    public static async Task<string> CreateAsync(RemoteFileStore store, string relative,
        Func<string, double, long, long, Task> report, CancellationToken ct)
    {
        string source = store.Resolve(relative);
        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source)) throw new IOException("请选择存在的文件或文件夹进行压缩");
        string name = (isDirectory ? new DirectoryInfo(source).Name : Path.GetFileName(source)).TrimEnd('\\', '/').Replace(":", "");
        if (string.IsNullOrEmpty(name)) name = "共享目录";
        string parent = source.Equals(store.Root, StringComparison.OrdinalIgnoreCase) ? store.Root : Path.GetDirectoryName(source)!;
        string output = Path.Combine(parent, name + ".zip");
        for (int i = 1; File.Exists(output) || Directory.Exists(output); i++) output = Path.Combine(parent, $"{name} ({i}).zip");
        string outputRelative = Path.GetRelativePath(store.Root, output).Replace('\\', '/');
        store.Resolve(outputRelative);
        string temp = Path.Combine(parent, ".remote-archive-" + Guid.NewGuid().ToString("N"));
        var items = new List<Source>();
        var timer = Stopwatch.StartNew();
        await report("扫描目录", 0, 0, 0);
        async Task ScanAsync(string path, string entry)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(store.Root, path).Replace('\\', '/');
            if (rel == ".") rel = "";
            store.Resolve(rel); // 每个子项都拒绝链接和目录越界。
            var info = new DirectoryInfo(path);
            items.Add(new(rel, entry + "/", true, 0, info.LastWriteTimeUtc));
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                ct.ThrowIfCancellationRequested();
                string childName = Path.GetFileName(child);
                if (childName.StartsWith(".remote-upload-", StringComparison.OrdinalIgnoreCase) || childName.StartsWith(".remote-archive-", StringComparison.OrdinalIgnoreCase)) continue;
                string childRel = Path.GetRelativePath(store.Root, child).Replace('\\', '/');
                store.Resolve(childRel);
                if (Directory.Exists(child)) await ScanAsync(child, entry + "/" + childName);
                else
                {
                    var file = new FileInfo(child);
                    items.Add(new(childRel, entry + "/" + childName, false, file.Length, file.LastWriteTimeUtc));
                }
                if (timer.ElapsedMilliseconds >= 200) { timer.Restart(); await report("扫描目录", 0, 0, 0); }
            }
        }
        if (isDirectory) await ScanAsync(source, name);
        else
        {
            var file = new FileInfo(source);
            items.Add(new(Path.GetRelativePath(store.Root, source).Replace('\\', '/'), name, false, file.Length, file.LastWriteTimeUtc));
        }
        long total = items.Sum(x => x.Length), completed = 0;
        Log.Information("压缩源扫描完成；源路径 {RemotePath}，条目 {Count}，原始字节 {Length}，ZIP {ArchivePath}", relative, items.Count, total, outputRelative);
        try
        {
            await using (var outputStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, true))
                {
                    byte[] buffer = new byte[131072];
                    await report("压缩", 0, 0, total);
                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        string full = store.Resolve(item.Relative);
                        var entry = archive.CreateEntry(item.Entry, CompressionLevel.Fastest);
                        var date = new DateTimeOffset(item.Modified, TimeSpan.Zero);
                        if (date.Year is >= 1980 and <= 2107) entry.LastWriteTime = date;
                        if (item.Directory) continue;
                        Log.Debug("压缩文件；路径 {RemotePath}，长度 {Length}", item.Relative, item.Length);
                        await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                        if (input.Length != item.Length || File.GetLastWriteTimeUtc(full) != item.Modified) throw new IOException("压缩源文件已变化，请重试：" + item.Relative);
                        await using var target = entry.Open();
                        long copied = 0; int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            if (copied + read > item.Length) throw new IOException("压缩期间文件长度变化：" + item.Relative);
                            await target.WriteAsync(buffer.AsMemory(0, read), ct);
                            copied += read; completed += read;
                            if (timer.ElapsedMilliseconds >= 200) { timer.Restart(); await report("压缩", total == 0 ? 0 : 99d * completed / total, completed, total); }
                        }
                        if (copied != item.Length || File.GetLastWriteTimeUtc(full) != item.Modified) throw new IOException("压缩期间文件变化：" + item.Relative);
                    }
                }
                await outputStream.FlushAsync(ct); outputStream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            store.Resolve(outputRelative);
            File.Move(temp, output); // 不覆盖同名文件；外部程序抢占名称时返回错误。
            Log.Information("ZIP 提交成功；源路径 {RemotePath}，ZIP {ArchivePath}，长度 {Length}", relative, outputRelative, new FileInfo(output).Length);
            return outputRelative;
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); Log.Debug("清理压缩临时文件；路径 {TemporaryPath}", temp); }
        }
    }
}
