using EverModern.Events;

namespace WebPhone.Domain;

public static class ValueNotifierExtensions
{
    extension<T>(IValueNotifier<T> notifier)
    {
        public Task WhenSatisfies(
        Func<T, bool> predicate,
        CancellationToken cancellationToken
    )
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            notifier.Subscribe(
                (newValue, sub) =>
                {
                    if (predicate(newValue))
                    {
                        tcs.TrySetResult();
                        sub.Dispose();
                    }
                }
            );

            if (predicate(notifier.Value))
                return Task.CompletedTask;

            return tcs.Task;
        }
    }
}
