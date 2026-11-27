using System.IO;
using GeminiDesktop.Helpers;
using Serilog;

namespace GeminiDesktop.Services;

/// <summary>
/// Application logging abstraction backed by Serilog.
/// </summary>
public interface ILoggingService : IDisposable
{
    string LogsDirectory { get; }
    void Information(string messageTemplate, params object?[] propertyValues);
    void Warning(string messageTemplate, params object?[] propertyValues);
    void Error(Exception? exception, string messageTemplate, params object?[] propertyValues);
    void Debug(string messageTemplate, params object?[] propertyValues);
}

/// <summary>
/// Configures and manages local rolling Serilog diagnostics without recording user credentials or conversation content.
/// </summary>
public sealed class LoggingService : ILoggingService
{
    private readonly ILogger _logger;
    private bool _disposed;

    public LoggingService(string? logsDirectoryOverride = null)
    {
        LogsDirectory = string.IsNullOrWhiteSpace(logsDirectoryOverride)
            ? ConfigurationHelper.GetLogsDirectory()
            : logsDirectoryOverride;

        ConfigurationHelper.EnsureDirectoryExists(LogsDirectory);

        var logFilePattern = Path.Combine(LogsDirectory, "mylaptop-.log");
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logFilePattern,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public string LogsDirectory { get; }

    public void Information(string messageTemplate, params object?[] propertyValues) =>
        _logger.Information(messageTemplate, propertyValues);

    public void Warning(string messageTemplate, params object?[] propertyValues) =>
        _logger.Warning(messageTemplate, propertyValues);

    public void Error(Exception? exception, string messageTemplate, params object?[] propertyValues) =>
        _logger.Error(exception, messageTemplate, propertyValues);

    public void Debug(string messageTemplate, params object?[] propertyValues) =>
        _logger.Debug(messageTemplate, propertyValues);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_logger is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
