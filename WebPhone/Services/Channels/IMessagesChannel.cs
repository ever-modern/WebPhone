using EverModern.Threading.Channels;
using WebPhone.Messages;

namespace WebPhone.Services.Channels;

public interface IMessagesChannel : IBroadcastChannel<IncomingMessage, OutgoingMessage> { }