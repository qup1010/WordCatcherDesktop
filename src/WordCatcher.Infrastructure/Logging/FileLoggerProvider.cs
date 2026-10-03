using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WordCatcher.Infrastructure.Logging;

public sealed partial class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _lock = new();

    [GeneratedRegex(@"(?i)bearer\s+[a-zA-Z0-9\-\._~+/]+=*", RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)api[-_]?key[""':\s=]+[a-zA-Z0-9\-\._~+/]+", RegexOptions.Compiled)]
    private static partial Regex ApiKeyRegex();

    public FileLoggerProvider(string? customLogDir = null)
    {
        if (string.IsNullOrEmpty(customLogDir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logDirectory = Path.Combine(appData, "WordCatcher", "logs");
        }
        else
        {
            _logDirectory = customLogDir;
        }

        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this);
    }

    private void WriteLog(string message)
    {
        lock (_lock)
        {
            try
            {
                var filePath = Path.Combine(_logDirectory, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(filePath, message + Environment.NewLine);
            }
            catch
            {
                // Never crash application on logging failure
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string categoryName, FileLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var msg = formatter(state, exception);
            // Sanitize sensitive info
            msg = BearerTokenRegex().Replace(msg, "Bearer [REDACTED]");
            msg = ApiKeyRegex().Replace(msg, "apiKey=[REDACTED]");

            var logLine = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_categoryName}] {msg}";
            if (exception != null)
            {
                logLine += Environment.NewLine + exception;
            }

            _provider.WriteLog(logLine);
        }
    }
}
