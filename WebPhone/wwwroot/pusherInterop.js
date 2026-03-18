const pusherConnections = new Map();
const messageQueues = new Map();

function ensurePusherLogging(enabled) {
  if (enabled && window.Pusher) {
    window.Pusher.logToConsole = true;
  }
}

function subscribe(key, cluster, channelName, eventName, enableLogging, authEndpoint, authSecret) {
  if (!window.Pusher) {
    throw new Error("Pusher JS SDK is not loaded.");
  }

  ensurePusherLogging(enableLogging);

  let entry = pusherConnections.get(channelName);
  if (!entry) {
    const options = { cluster };
    if (authEndpoint) {
      options.authEndpoint = authEndpoint;
      options.auth = {
        params: {
          key,
          secret: authSecret
        }
      };
    }

    const pusher = new window.Pusher(key, options);
    const channel = pusher.subscribe(channelName);
    entry = { pusher, channel, bindings: new Set() };
    pusherConnections.set(channelName, entry);
    messageQueues.set(channelName, []);
  }

  if (!entry.bindings.has(eventName)) {
    entry.channel.bind(eventName, (data) => {
      const queue = messageQueues.get(channelName) ?? [];
      queue.push({ type: eventName, payload: data ?? {} });
      messageQueues.set(channelName, queue);
    });
    entry.bindings.add(eventName);
  }

  if (eventName.startsWith("client-")) {
    const fallbackEvent = eventName.substring("client-".length);
    if (!entry.bindings.has(fallbackEvent)) {
      entry.channel.bind(fallbackEvent, (data) => {
        const queue = messageQueues.get(channelName) ?? [];
        queue.push({ type: fallbackEvent, payload: data ?? {} });
        messageQueues.set(channelName, queue);
      });
      entry.bindings.add(fallbackEvent);
    }
  }

  return new Promise((resolve, reject) => {
    const channel = entry.channel;
    channel.bind("pusher:subscription_succeeded", () => resolve(true));
    channel.bind("pusher:subscription_error", (status) => reject(new Error(`Subscription failed: ${status}`)));
  });
}

function poll(channelName) {
  const queue = messageQueues.get(channelName) ?? [];
  messageQueues.set(channelName, []);
  return queue;
}

function unsubscribe(channelName) {
  const entry = pusherConnections.get(channelName);
  if (!entry) {
    return;
  }

  entry.channel.unbind();
  entry.pusher.unsubscribe(channelName);
  entry.pusher.disconnect();
  pusherConnections.delete(channelName);
}

function publish(channelName, eventName, payload) {
  const entry = pusherConnections.get(channelName);
  if (!entry) {
    throw new Error(`Pusher channel '${channelName}' is not initialized.`);
  }

  if (!eventName.startsWith("client-")) {
    throw new Error("Client events must start with 'client-'.");
  }

  try {
    return entry.channel.trigger(eventName, payload ?? {});
  } catch {
    return false;
  }
}

window.pusherInterop = {
  subscribe,
  unsubscribe,
  publish,
  poll
};
