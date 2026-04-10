using EverModern.Threading.Channels;

namespace WebPhone.Services;

public interface IMessagesChannel : IBroadcastChannel<IncomingMessage, OutgoingMessage>
{

}