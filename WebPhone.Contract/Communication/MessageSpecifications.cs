using EverModern.Threading.Locks;

namespace WebPhone.Domain.Communication;

public static class MessageSpecifications
{
    public static ClientSendMessageSpecification<SentMessage> Send { get; } = new(
        "Send"
    );

    public static ServerSendMessageSpecification<ReceivedMessage> Push { get; } = new(
        "Push"
    );
}

public class ClientSendMessageSpecification<TMessage>
{
    public string Key { get; }
    internal ClientSendMessageSpecification(string key) { Key = key; }
    
}

public class ServerSendMessageSpecification<TMessage>
{
    public string Key { get; }
    internal ServerSendMessageSpecification(string key) { Key = key; }
}

public class TwoWayMessageSpecification<TClientMessage, TServerMessage>
{
    public string Key { get; }
    internal TwoWayMessageSpecification(string key) { Key = key; }
}
