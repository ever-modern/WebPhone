using Xunit.Abstractions;

namespace WebPhone.Tests;

public static class TestUtilities
{
    public static Func<string, TestLoggerProvider> ToLoggerFactory(
        this ITestOutputHelper output,
        List<(string Message, bool IsServer)>? logs = null
    ) =>
        prefix => new TestLoggerProvider(log =>
        {
            var logWithPrefix = $"[{prefix}]:{log}";
            output.WriteLine(logWithPrefix);
            logs?.Add((logWithPrefix, false));
        });
}
