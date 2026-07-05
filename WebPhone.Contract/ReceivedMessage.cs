using System.Text.Json;
using EverModern.Events;

namespace WebPhone.Domain;

public record ReceivedMessage(string Sender, MessageType Type, JsonElement Payload)
    : MessageContent(Type, Payload);

public static class NotifierExtensions
{
    public static ObservedValue<T> Transform<T>(this IValueNotifier<T> input, Func<T, T> transformer)
    {
        var observed = new ObservedValue<T>(input.Value);
        input.Subscribe(v => observed.Change(transformer(v)));
        return observed;
    }

    public static EventSource<T> Transform<T>(this INotifier<T> input, Func<T, T> transformer)
    {
        var eventSource = new EventSource<T>();
        input.Subscribe(v => eventSource.Invoke(transformer(v)));
        return eventSource;
    }
}
