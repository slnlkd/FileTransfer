using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Client;

public sealed class ClientUpdateService
{
    public static bool IsNewer(string latestText, Version current)
    {
        if (!Version.TryParse(latestText, out var latest)) return false;
        int latestBuild = Math.Max(0, latest.Build), currentBuild = Math.Max(0, current.Build);
        return latest.Major > current.Major
            || latest.Major == current.Major && (latest.Minor > current.Minor
                || latest.Minor == current.Minor && latestBuild > currentBuild);
    }
    public async Task ApplyUpdateAsync(Window owner, RemoteFileClient endpoint, RemoteUpdateInfo info)
    {
        string versionTag = string.Concat(info.Version.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));
        string tempRoot = CreateUpdateTempRoot(versionTag);
        var progressWindow = new UpdateProgressWindow { Owner = owner };
        progressWindow.Show();
        try
        {
            string zip = Path.Combine(tempRoot, info.FileName);
            var progress = new Progress<double>(value => progressWindow.Report($"正在下载 v{info.Version}… {value:F0}%", value));
            await endpoint.DownloadUpdateAsync(info, zip, progress);
            progressWindow.Report("正在解压更新包…", 100);
            string staging = Path.Combine(tempRoot, "app");
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zip, staging, true);
            string exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "FileTransfer.Client.exe");
            if (!File.Exists(Path.Combine(staging, exeName)))
            {
                var directories = Directory.GetDirectories(staging);
                if (directories.Length == 1 && File.Exists(Path.Combine(directories[0], exeName))) staging = directories[0];
                else
                {
                    // 兼容发布名调整：RemoteFiles.Client.exe 与 FileTransfer.Client.exe 可以互相迁移。
                    string renamed = exeName.Equals("RemoteFiles.Client.exe", StringComparison.OrdinalIgnoreCase)
                        ? "FileTransfer.Client.exe" : "RemoteFiles.Client.exe";
                    if (File.Exists(Path.Combine(staging, renamed))) exeName = renamed;
                    else throw new IOException("更新包结构无效，未找到 " + exeName);
                }
            }
            progressWindow.Report("正在准备重启…", 100);
            string target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            string script = Path.Combine(tempRoot, "apply-update.ps1");
            // PowerShell 5.1 只有带 BOM 才会按 UTF-8 读取脚本，否则日志中的中文会乱码。
            File.WriteAllText(script, UpdaterScript, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(), "-Staging", staging, "-Target", target, "-Exe", exeName, "-TempRoot", tempRoot })
                start.ArgumentList.Add(argument);
            Process.Start(start);
            progressWindow.Close();
            Application.Current.Shutdown();
        }
        catch
        {
            progressWindow.Close();
            try { Directory.Delete(tempRoot, true); }
            catch (Exception ex) { Log.Debug(ex, "清理更新临时目录失败；路径 {UpdateTemp}", tempRoot); }
            throw;
        }
    }
    private static string CreateUpdateTempRoot(string versionTag)
    {
        string name = versionTag + "-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(AppContext.BaseDirectory, "Update", name);
        Directory.CreateDirectory(path);
        Log.Information("更新临时目录已创建；路径 {UpdateTemp}", path);
        return path;
    }
    private const string UpdaterScript = """
param([int]$ProcessId,[string]$Staging,[string]$Target,[string]$Exe,[string]$TempRoot)
$ErrorActionPreference = 'Stop'
$updateRoot = Join-Path $Target 'Update'
$TempRoot = [IO.Path]::GetFullPath($TempRoot)
$Staging = [IO.Path]::GetFullPath($Staging)
if ([IO.Path]::GetDirectoryName($TempRoot) -ne $updateRoot -or
    -not $Staging.StartsWith($TempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '更新暂存目录不在部署目录下。'
}
$logDir = Join-Path $Target 'Logs'
try { [IO.Directory]::CreateDirectory($logDir) | Out-Null } catch { }
$log = Join-Path $logDir 'update.log'
function Write-UpdateLog([string]$Message) {
    try { [IO.File]::AppendAllText($log, "$(Get-Date -Format o) $Message`r`n", (New-Object Text.UTF8Encoding($false))) } catch { }
}
$backupRoot = Join-Path $TempRoot 'backup'
$applied = [Collections.Generic.Stack[object]]::new()
$keepBackup = $false
$updateLock = $null
try {
    $updateLock = [IO.File]::Open((Join-Path $updateRoot 'update.lock'), 'OpenOrCreate', 'Write', 'None')
    Write-UpdateLog "等待客户端退出：$ProcessId"
    $runningClient = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($runningClient -and -not $runningClient.WaitForExit(120000)) { throw '等待客户端退出超时。' }
    foreach ($source in [IO.Directory]::GetFiles($Staging, '*', 'AllDirectories')) {
        $relative = $source.Substring($Staging.Length + 1)
        $destination = Join-Path $Target $relative
        if ($relative -eq 'logging.json' -and [IO.File]::Exists($destination)) { continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $backup = $null
        if ([IO.File]::Exists($destination)) {
            $backup = Join-Path $backupRoot $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            [IO.File]::Replace($source, $destination, $backup)
        } else {
            [IO.File]::Move($source, $destination)
        }
        $applied.Push(@{ Destination = $destination; Backup = $backup })
    }
    Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
    Write-UpdateLog "更新完成并已重启"
} catch {
    Write-UpdateLog "更新失败：$_"
    while ($applied.Count -gt 0) {
        $file = $applied.Pop()
        try {
            if ($file.Backup) { [IO.File]::Copy($file.Backup, $file.Destination, $true) }
            else { [IO.File]::Delete($file.Destination) }
        } catch {
            $keepBackup = $true
            Write-UpdateLog "恢复文件失败，保留更新备份：$($file.Destination)：$_"
        }
    }
    if ($updateLock -and -not $keepBackup -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and
        (Test-Path -LiteralPath (Join-Path $Target $Exe))) {
        Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
    }
} finally {
    if ($updateLock) { $updateLock.Dispose() }
    if (-not $keepBackup) { Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
""";
}
