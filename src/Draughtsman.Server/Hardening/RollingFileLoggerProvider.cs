using System.Globalization;
using System.Text;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// An optional log file in <c>&lt;data&gt;/logs/</c> (AU-14, <c>logging.file.enabled</c>): one file per UTC day,
/// <c>draughtsman-yyyyMMdd.log</c>, continued in <c>draughtsman-yyyyMMdd-1.log</c> and so on once a file reaches
/// <c>maxFileBytes</c>; files older than <c>retainDays</c> are deleted as a new day starts and at start-up. It writes plain
/// lines (time, level, category, message, then the exception) and never the value of a property named <c>SetupCode</c>: that
/// one-time code is printed to the console so the operator can read it once, and a file kept for weeks is the wrong place
/// for it. Written as a small provider rather than a dependency: the project has none for logging and this is about a
/// hundred lines.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private static readonly string[] s_redactedProperties = ["SetupCode"];

    private readonly string _folder;
    private readonly int _retainDays;
    private readonly long _maxFileBytes;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _day;
    private int _part;

    public RollingFileLoggerProvider(string folder, int retainDays, long maxFileBytes, TimeProvider? time = null)
    {
        _folder = folder;
        _retainDays = retainDays;
        _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : 10 * 1024 * 1024;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Path of the file being written now, or null before the first line.</summary>
    public string? CurrentFile { get; private set; }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(this, categoryName);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(Abbreviation(level)).Append(' ').Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            try
            {
                StreamWriter writer = WriterFor(now);
                writer.WriteLine(line.ToString());
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk or a removed folder must not take requests down with it; the console log still has the line.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private StreamWriter WriterFor(DateTimeOffset now)
    {
        string day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        // A file an operator has renamed or removed under us (allowed by FileShare.Delete below) is not "full": start it afresh.
        var current = _writer is null ? null : new FileInfo(CurrentFile!);
        bool vanished = current is { Exists: false };
        if (_writer is not null && day == _day && !vanished && current!.Length < _maxFileBytes)
        {
            return _writer;
        }

        bool newDay = day != _day;
        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(_folder);
        if (newDay)
        {
            _part = 0;
            Prune(now);
        }
        else if (!vanished)
        {
            _part++;
        }

        _day = day;
        string path;
        while (true)
        {
            path = Path.Combine(_folder, _part == 0 ? $"draughtsman-{day}.log" : $"draughtsman-{day}-{_part}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < _maxFileBytes)
            {
                break;
            }

            _part++;
        }

        CurrentFile = path;

        // Windows refuses to open, rename or delete a file another handle holds unless that handle says it allows it:
        // ReadWrite lets an operator tail the live log, Delete lets a rotation tool or a clean-up rename or remove it.
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
        return _writer;
    }

    /// <summary>Deletes log files whose date is more than <c>retainDays</c> days old. Only files named like ours; nothing else in the folder is touched.</summary>
    private void Prune(DateTimeOffset now)
    {
        if (_retainDays <= 0 || !Directory.Exists(_folder))
        {
            return;
        }

        DateTime oldest = now.UtcDateTime.Date.AddDays(-_retainDays);
        foreach (string file in Directory.EnumerateFiles(_folder, "draughtsman-*.log"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length >= "draughtsman-yyyyMMdd".Length
                && DateTime.TryParseExact(name.AsSpan("draughtsman-".Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime fileDay)
                && fileDay < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next start-up.
                }
            }
        }
    }

    private static string Abbreviation(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (KeyValuePair<string, object?> property in properties)
                {
                    if (Array.IndexOf(s_redactedProperties, property.Key) >= 0 && property.Value?.ToString() is { Length: > 0 } secret)
                    {
                        message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
                    }
                }
            }

            owner.Write(category, logLevel, message, exception);
        }
    }
}
