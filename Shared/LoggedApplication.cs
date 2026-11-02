using System.Windows;
using Serilog;
using Transfer.Core;

namespace Transfer.Desktop;
public abstract class LoggedApplication : Application
{
    protected LoggedApplication(string role)
    {
        try { AppLogging.Initialize(role); }
        catch (Exception ex)
        {
            MessageBox.Show("无法初始化日志，程序不能启动：" + ex, "日志初始化失败");
            throw;
        }
        DispatcherUnhandledException += (_, e) =>
        {
            // 保持默认异常行为，避免程序在未知损坏状态下继续运行。
            Log.Fatal(e.Exception, "UI 线程发生未处理异常，程序即将退出");
            Log.CloseAndFlush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "应用域未处理异常；即将终止 {IsTerminating}，异常对象 {ExceptionObject}", e.IsTerminating, e.ExceptionObject);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "发现未观察的后台任务异常");
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Information("应用程序启动；参数 {@Arguments}", e.Args);
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try { Log.Information("应用程序退出；退出码 {ExitCode}", e.ApplicationExitCode); base.OnExit(e); }
        finally { Log.CloseAndFlush(); }
    }
}
