using System.Diagnostics;
using System.IO.Compression;
using Serilog;

namespace Transfer.Core.Remote;

internal static class RemoteArchives
{
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
    private static void SetEntryTime(ZipArchiveEntry entry, DateTime modifiedUtc)
    {
        var date = new DateTimeOffset(modifiedUtc, TimeSpan.Zero);
        if (date.Year is >= 1980 and <= 2107) entry.LastWriteTime = date;
    }
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
                    var skipped = new List<string>();
                    int writtenFiles = 0;
                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        string full = store.Resolve(item.Relative);
                        if (item.Directory)
                        {
                            var directoryEntry = archive.CreateEntry(item.Entry, CompressionLevel.Fastest);
                            SetEntryTime(directoryEntry, item.Modified);
                            continue;
                        }
                        Log.Debug("压缩文件；路径 {RemotePath}，长度 {Length}", item.Relative, item.Length);
                        FileStream? input = null;
                        bool volatileFile = false;
                        for (int attempt = 1; ; attempt++)
                        {
                            try
                            {
                                input = new FileStream(full, FileMode.Open, FileAccess.Read,
                                    attempt == 1 ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete, 131072, true);
                                volatileFile = attempt > 1;
                                break;
                            }
                            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 3)
                            {
                                // 文件可能正被日志进程写入，短暂等待后再试共享读取。
                                await Task.Delay(300, ct);
                            }
                            catch (IOException ex) when (IsSharingViolation(ex))
                            {
                                Log.Warning("压缩跳过被占用的文件；路径 {RemotePath}，原因 {Reason}", item.Relative, ex.Message);
                                skipped.Add(item.Entry);
                                break;
                            }
                        }
                        if (input is null) continue;
                        await using (input)
                        {
                            long length = item.Length;
                            if (volatileFile)
                            {
                                // 正在追加的文件按扫描时的长度截取快照，忽略后续写入。
                                length = Math.Min(item.Length, input.Length);
                            }
                            else if (input.Length != item.Length || File.GetLastWriteTimeUtc(full) != item.Modified)
                                throw new IOException("压缩源文件已变化，请重试：" + item.Relative);
                            var entry = archive.CreateEntry(item.Entry, CompressionLevel.Fastest);
                            SetEntryTime(entry, item.Modified);
                            await using var target = entry.Open();
                            long copied = 0; int read;
                            while (copied < length && (read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), ct)) > 0)
                            {
                                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                                copied += read; completed += read;
                                if (timer.ElapsedMilliseconds >= 200) { timer.Restart(); await report("压缩", total == 0 ? 0 : 99d * completed / total, completed, total); }
                            }
                            if (!volatileFile && (copied != length || File.GetLastWriteTimeUtc(full) != item.Modified))
                                throw new IOException("压缩期间文件变化：" + item.Relative);
                            writtenFiles++;
                        }
                    }
                    // 单文件或全部文件都无法读取时给出明确原因，避免生成空压缩包。
                    if (writtenFiles == 0 && skipped.Count > 0)
                        throw new IOException("文件正在被其他进程使用，请稍后重试：" + string.Join("、", skipped));
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
