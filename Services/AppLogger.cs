using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Application logger that wraps Serilog with ILogger interface
/// Provides colored console output and structured logging capabilities
/// </summary>
public class AppLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly Serilog.ILogger _serilogLogger;
    private readonly string _categoryName;

    public AppLogger(string categoryName)
    {
        _categoryName = categoryName;
        
        // Configure Serilog for console output with colors
        _serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
                theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
            .CreateLogger();
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _serilogLogger.IsEnabled(ConvertLogLevel(logLevel));
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var serilogLevel = ConvertLogLevel(logLevel);

        _serilogLogger.Write(serilogLevel, exception, message);
    }

    private static LogEventLevel ConvertLogLevel(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            LogLevel.None => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }
}
