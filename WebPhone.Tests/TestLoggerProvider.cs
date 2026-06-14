using Microsoft.Extensions.Logging;

namespace WebPhone.Tests;

public sealed class TestLoggerProvider(
    Action<string> sink
) : ILoggerProvider, ILoggerFactory
{
    private readonly object _logsLock = new();

    public ILogger CreateLogger(string categoryName) => new TestLogger(sink, _logsLock, categoryName);

    public ILogger<T> CreateLogger<T>(string categoryName) => new TestLogger<T>(sink, _logsLock, categoryName);

    public void AddProvider(ILoggerProvider provider) {}

    public void Dispose() {}

    class TestLogger<T>(
        Action<string> sink,
        object logsLock,
        string category
    ) : TestLogger(sink, logsLock, category), ILogger<T> {}

    private class TestLogger(
        Action<string> sink,
        object logsLock,
        string category
    ) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var log = $"[{logLevel}] {category}: {formatter(state, exception)};";
            lock (logsLock)
            {
                sink(log);
            }
            Console.WriteLine($"[SERVER]: {log}");
        }
    }
}
