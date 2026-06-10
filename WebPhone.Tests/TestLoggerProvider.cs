using Microsoft.Extensions.Logging;

namespace WebPhone.Tests;

public sealed class TestLoggerProvider : ILoggerProvider
{
    private readonly List<string> _logs;

    public TestLoggerProvider(List<string> logs)
    {
        _logs = logs;
    }

    public ILogger CreateLogger(string categoryName)
        => new TestLogger(_logs, categoryName);

    public void Dispose() { }

    private sealed class TestLogger : ILogger
    {
        private readonly List<string> _logs;
        private readonly string _category;

        public TestLogger(List<string> logs, string category)
        {
            _logs = logs;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _logs.Add($"[{logLevel}] {_category}: {formatter(state, exception)}");
        }
    }
}
