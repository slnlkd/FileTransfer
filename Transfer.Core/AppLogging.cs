using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Transfer.Core;
public sealed class LoggingOptions
{
    public string MinimumLevel { get; set; } = "Debug";
    public string? Directory { get; set; }
    public int RetainedFileCountLimit { get; set; } = 31;
    public long FileSizeLimitBytes { get; set; } = 50 * 1024 * 1024;
}
public static class AppLogging
{
    public static string LogDirectory { get; private set; } = "";
    public static void Initialize(string application, string? configurationPath = null)
    {
        string config = configurationPath ?? Path.Combine(AppContext.BaseDirectory, "logging.json");
        var options = new LoggingOptions();
        Exception? configurationError = null;
        try
        {
            if (File.Exists(config)) options = JsonSerializer.Deserialize<LoggingOptions>(File.ReadAllText(config),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("日志配置为空");
            if (!Enum.TryParse<LogEventLevel>(options.MinimumLevel, true, out var parsed) || !Enum.IsDefined(parsed) ||
                options.RetainedFileCountLimit <= 0 || options.FileSizeLimitBytes <= 0)
                throw new InvalidDataException("日志级别、文件保留数量或大小不合法");
        }
        catch (Exception ex) { configurationError = ex; options = new(); }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileTransferDemo", "Logs", application);
        string directory = fallback;
        Exception? directoryError = null;
        try
        {
            string? configuredDirectory = Environment.GetEnvironmentVariable("FILETRANSFER_LOG_DIRECTORY") ?? options.Directory;
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
                directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory), AppContext.BaseDirectory);
            Probe(directory);
        }
        catch (Exception ex)
        {
            directoryError = ex;
            try { directory = fallback; Probe(directory); }
            catch (Exception fallbackError)
            {
                directoryError = new AggregateException(ex, fallbackError);
                directory = Path.Combine(AppContext.BaseDirectory, "Logs", application);
                Probe(directory);
            }
        }
        LogDirectory = directory;
        // Serilog 自身故障不能递归写回 Serilog，单独保留诊断文件。
        var selfLogLock = new object();
        Serilog.Debugging.SelfLog.Enable(message =>
        {
            try
            {
                lock (selfLogLock)
                {
                    string path = Path.Combine(directory, $"serilog-self-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
                    Directory.CreateDirectory(directory);
                    if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, path + ".previous", true);
                    File.AppendAllText(path, message, Encoding.UTF8);
                }
            }
            catch { Debug.WriteLine(message); }
        });
        const string template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} | {Properties:j}{NewLine}{Exception}";
        var level = Enum.Parse<LogEventLevel>(options.MinimumLevel, true);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Is(level).Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application).Enrich.WithProperty("Machine", Environment.MachineName)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId).Enrich.With(new ThreadEnricher())
            .WriteTo.File(Path.Combine(directory, "app-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: options.FileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCountLimit, shared: true, buffered: false,
                encoding: new UTF8Encoding(false), outputTemplate: template)
            .WriteTo.File(Path.Combine(directory, "error-.log"), restrictedToMinimumLevel: LogEventLevel.Error,
                rollingInterval: RollingInterval.Day, fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true, retainedFileCountLimit: options.RetainedFileCountLimit,
                shared: true, buffered: false, encoding: new UTF8Encoding(false), outputTemplate: template)
            .CreateLogger();
        if (configurationError != null) Log.Warning(configurationError, "读取日志配置失败，使用默认配置；配置路径 {ConfigurationPath}", config);
        if (directoryError != null) Log.Warning(directoryError, "日志目录不可用，已回退至 {LogDirectory}", directory);
        Log.Information("日志初始化完成；目录 {LogDirectory}，级别 {MinimumLevel}，保留文件数 {RetainedFiles}，单文件上限 {FileSizeLimitBytes}，版本 {Version}，运行时 {Runtime}，工作目录 {WorkingDirectory}",
            directory, level, options.RetainedFileCountLimit, options.FileSizeLimitBytes,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(), Environment.Version, Environment.CurrentDirectory);
    }
    public static string GetSettingsPath(string fileName)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileTransferDemo");
        try { Probe(directory); }
        catch (Exception ex)
        {
            directory = Path.Combine(AppContext.BaseDirectory, "Settings");
            Probe(directory);
            Log.Warning(ex, "默认配置目录不可写，使用 {SettingsDirectory}", directory);
        }
        return Path.Combine(directory, fileName);
    }
    private static void Probe(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".probe");
        using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
    private sealed class ThreadEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory) =>
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}


