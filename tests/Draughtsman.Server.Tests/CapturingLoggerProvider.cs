using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>Keeps every log line a test server writes, at every level, so a test can assert what was (and was not) logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, _lines);
    }

    public void Dispose()
    {
    }

    public sealed record LogLine(string Category, LogLevel Level, string Message)
    {
        public override string ToString()
        {
            return $"{Level} {Category}: {Message}";
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(new LogLine(category, logLevel, formatter(state, exception)));
        }
    }
}
