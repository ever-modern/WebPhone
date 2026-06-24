using EverModern.Threading.Channels;
using WebPhone.Messages;

namespace WebPhone.Channels;

public interface IMessagesChannel : IBroadcastChannel<IncomingMessage, OutgoingMessage> { }