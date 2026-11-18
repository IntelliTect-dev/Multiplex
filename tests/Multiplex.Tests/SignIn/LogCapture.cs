using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Multiplex.Tests.SignIn;

/// <summary>One captured log entry: the formatted message plus every structured value.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)
{
    /// <summary>Everything the entry would put in a log sink, for "never logs X" checks.</summary>
    public string AllText => Message + "\n" + string.Join("\n", Values.Select(v => $"{v.Key}={v.Value}"));
}

/// <summary>A logger provider that keeps every entry, so a test can check what was (and was not) logged.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    /// <summary>A typed logger writing into this capture, for constructing a class by hand.</summary>
    public ILogger<T> For<T>() => new TypedLogger<T>(CreateLogger(typeof(T).FullName!));

    public string AllText => string.Join("\n", Entries.Select(e => e.AllText));

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(LogCapture capture, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            var message = formatter(state, exception) + (exception is null ? "" : "\n" + exception);
            capture.Entries.Enqueue(new LogEntry(category, logLevel, message, [.. values]));
        }
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
