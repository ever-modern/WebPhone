using System.Threading.Channels;

namespace WebPhone.Registration;

/// <summary>
/// Allows to interact with an external channel, which is not owned by the current component, but can be used to send and receive messages through it.
/// </summary>
/// <typeparam name="TMessage"></typeparam>
public interface IExternalChannel<TMessage>
{
    ChannelWriter<TMessage> Writer { get; }

    ChannelReader<TMessage> Reader { get; }
}
