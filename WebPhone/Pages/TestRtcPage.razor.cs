using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using WebPhone.Background;
using WebPhone.Channels;
using WebPhone.Data;

namespace WebPhone.UI.Pages;

public partial class TestRtcPage
{
    [Inject]
    protected IRtcConnector RtcConnector { get; set; }

    [Inject]
    protected ILoggerFactory LoggerFactory { get; set; }

    async Task<(PeerConnectionsDispatcher Dispatcher, Subscription Dispose)> CreateDispatcher(
        string peerId
    )
    {
        var backendClient = new BackendClient("http://localhost:5194", new StaticProfile(peerId));
        PeerConnectionsDispatcher dispatcher = new(RtcConnector, LoggerFactory, backendClient);
        IncomingConnectionsHandler incomingConnectionsHandler = new(
            dispatcher,
            LoggerFactory.CreateLogger<IncomingConnectionsHandler>()
        );
        var hub = await backendClient.OpenHubConnectionAsync();

        var channel = await BackendMessagesChannel.BindAsync(hub);
        await incomingConnectionsHandler.StartReadingAsync(channel);
        return (
            dispatcher,
            new(() => Task.Run(async () =>
            {
                incomingConnectionsHandler.Dispose();
                await channel.DisposeAsync();
                await hub.DisposeAsync();
            }))
        );
    }

    class StaticProfile(string userId) : IProfile
    {
        public User User => new(userId, userId);

        public INotifier<User> UserChanged { get; } = new EventSource<User>();
    }
}
