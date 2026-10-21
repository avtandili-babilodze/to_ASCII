using System.Text;
using Microsoft.Extensions.Logging;

namespace AsciiVision.App.Services;

/// <summary>Minimal daily log file under %LOCALAPPDATA%\AsciiVision\logs (no extra dependencies).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public FileLoggerProvider(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, $"asciivision-{DateTime.Now:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null; // logging must never prevent the app from starting
        }
    }

    public string? LogPath { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            int dot = category.LastIndexOf('.');
            _category = dot >= 0 ? category[(dot + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..4].ToUpperInvariant()}] {_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }
    }
}
