using Microsoft.Extensions.Logging;

namespace WebPhone.Tests.Provision;

public class MockLogger<T>(string? categoryName = null) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return new NullScope();
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var message = formatter(state, exception);
        Console.WriteLine(
            $"[{DateTime.UtcNow:O}] [{categoryName}] [{logLevel}] {message}{(exception is null ? string.Empty : $" | {exception}")}"
        );
    }

    class NullScope : IDisposable
    {
        public void Dispose() { }
    }
}
