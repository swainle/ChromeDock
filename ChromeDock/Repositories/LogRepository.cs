using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace ChromeDock.Repositories;

public sealed class LogRepository
{
    private readonly Logger _logger;

    private LogRepository(string name)
    {
        this._logger = LogManager.GetLogger(name);
    }

    public static LogRepository For<T>()
    {
        return new LogRepository(typeof(T).FullName!);
    }

    public static void Initialize()
    {
        var executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "ChromeDock";
        var target = new FileTarget("file")
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Logs", "${date:format=yyyy-MM-dd}", executable + ".log"),
            ArchiveAboveSize = 20 * 1024 * 1024,
            ArchiveSuffixFormat = "_{0:000}",
            CreateDirs = true,
            Layout = "${longdate} | ${level:uppercase=true} | ${threadid} | ${logger} | ${message} ${exception:format=tostring}"
        };
        var configuration = new LoggingConfiguration();
        configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
        LogManager.Configuration = configuration;
        For<LogRepository>().Info("=== 启动 {0}，版本 {1}，进程 {2} ===",
            executable, typeof(App).Assembly.GetName().Version, Environment.ProcessId);
    }

    public static void Shutdown()
    {
        LogManager.Shutdown();
    }

    public void Debug(string message, params object?[] args) => this._logger.Debug(message, args);
    public void Info(string message, params object?[] args) => this._logger.Info(message, args);
    public void Warn(string message, params object?[] args) => this._logger.Warn(message, args);
    public void Warn(Exception exception, string message, params object?[] args) => this._logger.Warn(exception, message, args);
    public void Error(Exception exception, string message, params object?[] args) => this._logger.Error(exception, message, args);
    public void Fatal(Exception? exception, string message) => this._logger.Fatal(exception, message);
}
