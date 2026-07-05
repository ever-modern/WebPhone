using EverModern.Events;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Tests.Provision;
using Xunit.Abstractions;

namespace WebPhone.Tests;

using PeerDispatcherData = (
    WebPhone.ContactsDispatcher ContactsDispatcher,
    PeerConnectionsDispatcher Connections,
    string UserId
);

public class ContactsDispatcherIntegrationTests(ITestOutputHelper output)
    : IntegrationWithBackendTestsSet(output)
{
    async IAsyncEnumerable<PeerDispatcherData> GenerateDispatcherAsync()
    {
        ObservedValue<IReadOnlyList<Contact>> contacts = new([]);
        
        await foreach (var (connectionDispatcher, peerId) in GeneratePeers())
        {
            var contactsRepo = new MockContactsRepository(peerId, contacts);
            var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddProvider(CreateLoggerFactory($"CD-{peerId}"))
            );
            
            var dispatcher = new ContactsDispatcher(
                connectionDispatcher,
                contactsRepo,
                loggerFactory
            ).Started();

            contacts.Change([
                .. contacts.Value,
                new Contact(peerId, peerId, DateTimeOffset.UtcNow),
            ]);

            yield return (dispatcher, connectionDispatcher, peerId);
        }
    }

    async Task<(PeerDispatcherData, PeerDispatcherData)> CreatePairAsync()
    {
        var results = await GenerateDispatcherAsync().Take(2).ToArrayAsync();
        return (results[0], results[1]);
    }

    static ContactManager? FindContact(PhoneState state, string contactId) =>
        state.Contacts.FirstOrDefault(c => c.Contact.Id == contactId);

    // ── Connect ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectStages()
    {
        var ((dispatcher1, connector1, peer1), (dispatcher2, connector2, peer2)) = await CreatePairAsync();
        List<InteractionState> statesFirst = [];
        using var _ = dispatcher1.State.Subscribe(newState =>
        {
            var otherPeerManager = newState.Contacts.FirstOrDefault(c => c.Contact.Id == peer2);
            if (otherPeerManager is not null)
            {
                statesFirst.Add(otherPeerManager.Interaction);
            }
        });
        List<InteractionState> statesSecond = [];
        using var __ = dispatcher2.State.Subscribe(newState =>
        {
            var otherPeerManager = newState.Contacts.FirstOrDefault(c => c.Contact.Id == peer1);
            if (otherPeerManager is not null)
            {
                statesSecond.Add(otherPeerManager.Interaction);
            }
        });

        var contactManager1 = () => dispatcher1.State.Value.Contacts.First(contact =>
            contact.Contact.Id == peer2
        );
        var contactManager2 = () => dispatcher2.State.Value.Contacts.First(contact =>
            contact.Contact.Id == peer1
        );

        contactManager1().Connect!();        

        await Task.Delay(1000);

        contactManager1().AudioCall();

        await Task.Delay(1000);

        contactManager2().AcceptCall();

        await Task.Delay(1000);

        Assert.Contains(statesFirst, s => s is InteractionState.Connecting);
        Assert.Contains(statesFirst, s => s is InteractionState.Connected);
        Assert.Contains(statesFirst, s => s is InteractionState.Calling);
        Assert.Contains(statesFirst, s => s is InteractionState.OnCall);

        Assert.Contains(statesSecond, s => s is InteractionState.Connecting);
        Assert.Contains(statesSecond, s => s is InteractionState.Connected);
        Assert.Contains(statesSecond, s => s is InteractionState.ReceivingCall);
        Assert.Contains(statesSecond, s => s is InteractionState.OnCall);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_TwoPeers_BothSeeConnected()
    {
        var ct = Timeout.Token;
        var (first, second) = await CreatePairAsync();

        await first.Connections.ConnectAsync(second.UserId, ct);

        await WaitForStateAsync(
            second.ContactsDispatcher,
            first.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var firstContact = FindContact(first.ContactsDispatcher.State.Value, second.UserId);
        var secondContact = FindContact(second.ContactsDispatcher.State.Value, first.UserId);

        Assert.NotNull(firstContact);
        Assert.NotNull(secondContact);
        Assert.IsType<InteractionState.Connected>(firstContact!.Interaction);
        Assert.IsType<InteractionState.Connected>(secondContact!.Interaction);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_CalleeSeesConnected_AfterCallerInitiates()
    {
        var ct = Timeout.Token;
        var (first, second) = await CreatePairAsync();

        await first.Connections.ConnectAsync(second.UserId, ct);

        await WaitForStateAsync(
            second.ContactsDispatcher,
            first.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var calleeContact = FindContact(second.ContactsDispatcher.State.Value, first.UserId);
        Assert.NotNull(calleeContact);
        Assert.IsType<InteractionState.Connected>(calleeContact!.Interaction);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_CallerSeesConnected_AfterConnectionCompletes()
    {
        var ct = Timeout.Token;
        var (first, second) = await CreatePairAsync();

        await first.Connections.ConnectAsync(second.UserId, ct);

        await WaitForStateAsync(
            first.ContactsDispatcher,
            second.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var callerContact = FindContact(first.ContactsDispatcher.State.Value, second.UserId);
        Assert.NotNull(callerContact);
        Assert.IsType<InteractionState.Connected>(callerContact!.Interaction);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_BothDirections_WhenInitiatorIsSecondPeer()
    {
        var ct = Timeout.Token;
        var (first, second) = await CreatePairAsync();

        await second.Connections.ConnectAsync(first.UserId, ct);

        await WaitForStateAsync(
            first.ContactsDispatcher,
            second.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var firstContact = FindContact(first.ContactsDispatcher.State.Value, second.UserId);
        var secondContact = FindContact(second.ContactsDispatcher.State.Value, first.UserId);

        Assert.IsType<InteractionState.Connected>(firstContact!.Interaction);
        Assert.IsType<InteractionState.Connected>(secondContact!.Interaction);
    }

    // ── Repeated connect with same peer ───────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task Connect_Repeatedly_SamePeer_StaysConnected()
    {
        var ct = Timeout.Token;
        var (first, second) = await CreatePairAsync();

        for (int i = 0; i < 5; i++)
        {
            await first.Connections.ConnectAsync(second.UserId, ct);
        }

        await WaitForStateAsync(
            second.ContactsDispatcher,
            first.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var firstContact = FindContact(first.ContactsDispatcher.State.Value, second.UserId);
        var secondContact = FindContact(second.ContactsDispatcher.State.Value, first.UserId);

        Assert.IsType<InteractionState.Connected>(firstContact!.Interaction);
        Assert.IsType<InteractionState.Connected>(secondContact!.Interaction);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_Repeatedly_FromBothSides_StaysConnected()
    {
        var ct = Timeout.Token;
        var (first, second) = await CreatePairAsync();

        for (int i = 0; i < 5; i++)
        {
            if (i % 2 == 0)
                await first.Connections.ConnectAsync(second.UserId, ct);
            else
                await second.Connections.ConnectAsync(first.UserId, ct);
        }

        await WaitForStateAsync(
            first.ContactsDispatcher,
            second.UserId,
            s => s is InteractionState.Connected,
            ct
        );
        await WaitForStateAsync(
            second.ContactsDispatcher,
            first.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        Assert.IsType<InteractionState.Connected>(
            FindContact(first.ContactsDispatcher.State.Value, second.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.Connected>(
            FindContact(second.ContactsDispatcher.State.Value, first.UserId)!.Interaction
        );
    }

    // ── Many peers ────────────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Connect_ThreePeers_Sequential_AllSeeEachOther()
    {
        var ct = Timeout.Token;

        var peers = await GenerateDispatcherAsync().Take(3).ToArrayAsync();
        var (a, b, c) = (peers[0], peers[1], peers[2]);

        await a.Connections.ConnectAsync(b.UserId, ct);
        await WaitForStateAsync(
            b.ContactsDispatcher,
            a.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        await b.Connections.ConnectAsync(c.UserId, ct);
        await WaitForStateAsync(
            c.ContactsDispatcher,
            b.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        Assert.IsType<InteractionState.Connected>(
            FindContact(a.ContactsDispatcher.State.Value, b.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.Connected>(
            FindContact(b.ContactsDispatcher.State.Value, a.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.Connected>(
            FindContact(b.ContactsDispatcher.State.Value, c.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.Connected>(
            FindContact(c.ContactsDispatcher.State.Value, b.UserId)!.Interaction
        );
    }

    // ── Calling ───────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task Call_CalleeSeesReceivingCall()
    {
        using var innerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = innerCts.Token;
        var (first, second) = await CreatePairAsync();
        var (caller, callee) = (first, second);

        await caller.Connections.ConnectAsync(callee.UserId, ct);
        await WaitForStateAsync(
            caller.ContactsDispatcher,
            callee.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var callerContact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(callerContact?.AudioCall);
        callerContact!.AudioCall!.Invoke();

        await WaitForStateAsync(
            callee.ContactsDispatcher,
            caller.UserId,
            s => s is InteractionState.ReceivingCall,
            ct,
            pollIntervalMs: 50
        );

        var calleeContact = FindContact(callee.ContactsDispatcher.State.Value, caller.UserId);
        Assert.NotNull(calleeContact);
        Assert.IsType<InteractionState.ReceivingCall>(calleeContact!.Interaction);
    }

    [Fact(Timeout = 30_000)]
    public async Task Call_CallerSeesCalling()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (first, second) = await CreatePairAsync();
        var (caller, callee) = (first, second);

        await caller.Connections.ConnectAsync(callee.UserId, ct);
        await WaitForStateAsync(
            caller.ContactsDispatcher,
            callee.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var callerContact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(callerContact?.AudioCall);
        callerContact!.AudioCall!.Invoke();

        // Give Call() time to run on thread pool
        await Task.Delay(200, ct);

        await WaitForStateAsync(
            caller.ContactsDispatcher,
            callee.UserId,
            s => s is InteractionState.Calling,
            ct,
            pollIntervalMs: 50
        );

        var contact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(contact);
        Assert.IsType<InteractionState.Calling>(contact!.Interaction);
    }

    [Fact(Timeout = 30_000)]
    public async Task Call_AcceptCall_BothSeeOnCall()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;
        var (first, second) = await CreatePairAsync();
        var (caller, callee) = (first, second);

        await caller.Connections.ConnectAsync(callee.UserId, ct);
        await WaitForStateAsync(
            caller.ContactsDispatcher,
            callee.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var callerContact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(callerContact?.AudioCall);
        callerContact!.AudioCall!.Invoke();

        await WaitForStateAsync(
            callee.ContactsDispatcher,
            caller.UserId,
            s => s is InteractionState.ReceivingCall,
            ct,
            pollIntervalMs: 50
        );

        var calleeContact = FindContact(callee.ContactsDispatcher.State.Value, caller.UserId);
        Assert.NotNull(calleeContact?.AcceptCall);
        calleeContact!.AcceptCall!.Invoke();

        await Task.WhenAll(
            WaitForStateAsync(
                caller.ContactsDispatcher,
                callee.UserId,
                s => s is InteractionState.OnCall,
                ct,
                pollIntervalMs: 50
            ),
            WaitForStateAsync(
                callee.ContactsDispatcher,
                caller.UserId,
                s => s is InteractionState.OnCall,
                ct,
                pollIntervalMs: 50
            )
        );

        Assert.IsType<InteractionState.OnCall>(
            FindContact(caller.ContactsDispatcher.State.Value, callee.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.OnCall>(
            FindContact(callee.ContactsDispatcher.State.Value, caller.UserId)!.Interaction
        );
    }

    [Fact(Timeout = 30_000)]
    public async Task Call_RejectCallee_BothReturnToConnected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;
        var (first, second) = await CreatePairAsync();
        var (caller, callee) = (first, second);

        await caller.Connections.ConnectAsync(callee.UserId, ct);
        await WaitForStateAsync(
            caller.ContactsDispatcher,
            callee.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var callerContact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(callerContact?.AudioCall);
        callerContact!.AudioCall!.Invoke();

        await WaitForStateAsync(
            callee.ContactsDispatcher,
            caller.UserId,
            s => s is InteractionState.ReceivingCall,
            ct,
            pollIntervalMs: 50
        );

        var calleeContact = FindContact(callee.ContactsDispatcher.State.Value, caller.UserId);
        Assert.NotNull(calleeContact?.DeclineCall);
        calleeContact!.DeclineCall!.Invoke();

        await Task.WhenAll(
            WaitForStateAsync(
                caller.ContactsDispatcher,
                callee.UserId,
                s => s is InteractionState.Connected,
                ct,
                pollIntervalMs: 50
            ),
            WaitForStateAsync(
                callee.ContactsDispatcher,
                caller.UserId,
                s => s is InteractionState.Connected,
                ct,
                pollIntervalMs: 50
            )
        );

        Assert.IsType<InteractionState.Connected>(
            FindContact(caller.ContactsDispatcher.State.Value, callee.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.Connected>(
            FindContact(callee.ContactsDispatcher.State.Value, caller.UserId)!.Interaction
        );
    }

    [Fact(Timeout = 30_000)]
    public async Task Call_CancelByCaller_BothReturnToConnected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;
        var (first, second) = await CreatePairAsync();
        var (caller, callee) = (first, second);

        await caller.Connections.ConnectAsync(callee.UserId, ct);
        await WaitForStateAsync(
            caller.ContactsDispatcher,
            callee.UserId,
            s => s is InteractionState.Connected,
            ct
        );

        var callerContact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(callerContact?.AudioCall);
        callerContact!.AudioCall!.Invoke();

        await WaitForStateAsync(
            callee.ContactsDispatcher,
            caller.UserId,
            s => s is InteractionState.ReceivingCall,
            ct,
            pollIntervalMs: 50
        );

        var stoppingContact = FindContact(caller.ContactsDispatcher.State.Value, callee.UserId);
        Assert.NotNull(stoppingContact?.StopCalling);
        stoppingContact!.StopCalling!.Invoke();

        await Task.WhenAll(
            WaitForStateAsync(
                caller.ContactsDispatcher,
                callee.UserId,
                s => s is InteractionState.Connected,
                ct,
                pollIntervalMs: 50
            ),
            WaitForStateAsync(
                callee.ContactsDispatcher,
                caller.UserId,
                s => s is InteractionState.Connected,
                ct,
                pollIntervalMs: 50
            )
        );

        Assert.IsType<InteractionState.Connected>(
            FindContact(caller.ContactsDispatcher.State.Value, callee.UserId)!.Interaction
        );
        Assert.IsType<InteractionState.Connected>(
            FindContact(callee.ContactsDispatcher.State.Value, caller.UserId)!.Interaction
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    static async Task WaitForStateAsync(
        ContactsDispatcher dispatcher,
        string contactId,
        Func<InteractionState, bool> predicate,
        CancellationToken ct,
        int pollIntervalMs = 10
    )
    {
        while (!ct.IsCancellationRequested)
        {
            var contact = FindContact(dispatcher.State.Value, contactId);
            if (contact is not null && predicate(contact.Interaction))
                return;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = dispatcher.State.Subscribe(() =>
            {
                var c = FindContact(dispatcher.State.Value, contactId);
                if (c is not null && predicate(c.Interaction))
                    tcs.TrySetResult();
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(pollIntervalMs);

            try
            {
                await tcs.Task.WaitAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }
}
