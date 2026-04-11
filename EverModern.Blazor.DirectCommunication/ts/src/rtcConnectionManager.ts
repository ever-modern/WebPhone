type RtcMgrDotNetReference = {
  invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};

type ByteSubscriber = (data: Uint8Array) => void;

type RtcConnectionManager = {
  enableAudio(): void;
  disableAudio(): void;
  enableVideo(): void;
  disableVideo(): void;
  writeBytes(input: Uint8Array | ArrayBuffer): void;
  subscribeBytes(callback: ByteSubscriber | RtcMgrDotNetReference): number;
  unsubscribeBytes(subscriptionId: number): void;
  getLocalAnswer(): RTCSessionDescription | null;
  close(): void;
};

type RtcConnectionManagerFactory = {
  initiateConnectionAsync(dotnetCallback: RtcMgrDotNetReference, onStateChanged: RtcMgrDotNetReference): Promise<RtcConnectionManager>;
  acceptConnectionAsync(offer: RTCSessionDescriptionInit, onStateChanged: RtcMgrDotNetReference): Promise<RtcConnectionManager>;
};

interface Window {
  rtcConnectionManagerInterop: RtcConnectionManagerFactory;
}

const rtcConnectionManagerPrototype: Record<string, unknown> = {};

function toBytes(data: unknown): Promise<Uint8Array | null> {
  if (data instanceof Uint8Array) {
    return Promise.resolve(data);
  }

  if (data instanceof ArrayBuffer) {
    return Promise.resolve(new Uint8Array(data));
  }

  if (ArrayBuffer.isView(data)) {
    return Promise.resolve(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
  }

  if (data instanceof Blob) {
    return data.arrayBuffer().then((buffer) => new Uint8Array(buffer));
  }

  return Promise.resolve(null);
}

function notifyBytes(subscribers: Map<number, ByteSubscriber>, data: unknown): void {
  void toBytes(data).then((bytes) => {
    if (!bytes) {
      return;
    }

    subscribers.forEach((subscriber) => {
      subscriber(bytes);
    });
  });
}

function waitForIceGatheringCompleteRtcMgr(connection: RTCPeerConnection, timeoutMs = 5000): Promise<void> {
  if (connection.iceGatheringState === "complete") {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    const handler = () => {
      if (connection.iceGatheringState === "complete") {
        connection.removeEventListener("icegatheringstatechange", handler);
        resolve();
      }
    };

    connection.addEventListener("icegatheringstatechange", handler);
    setTimeout(() => {
      connection.removeEventListener("icegatheringstatechange", handler);
      resolve();
    }, timeoutMs);
  });
}

type CreatedConnection = {
  manager: RtcConnectionManager;
  peerConnection: RTCPeerConnection;
  wireDataChannel(channel: RTCDataChannel): void;
  setLocalAnswer(description: RTCSessionDescription | null): void;
};

function createRtcManagerConnection(stateCallback: RtcMgrDotNetReference): CreatedConnection {
  const peerConnection = new RTCPeerConnection();
  peerConnection.addTransceiver("audio", { direction: "sendrecv" });
  peerConnection.addTransceiver("video", { direction: "sendrecv" });

  let dataChannel: RTCDataChannel | null = null;
  let localAnswer: RTCSessionDescription | null = null;
  const subscribers = new Map<number, ByteSubscriber>();
  let nextSubscriberId = 0;
  let isClosed = false;

  const wireDataChannel = (channel: RTCDataChannel): void => {
    dataChannel = channel;
    channel.binaryType = "arraybuffer";
    channel.onmessage = (event) => {
      notifyBytes(subscribers, event.data);
    };
  };

  const manager = Object.create(rtcConnectionManagerPrototype) as RtcConnectionManager;
  manager.enableAudio = (): void => {
    const sender = peerConnection.getSenders().find((item) => item.track?.kind === "audio");
    if (sender?.track) {
      sender.track.enabled = true;
    }
  };

  manager.disableAudio = (): void => {
    const sender = peerConnection.getSenders().find((item) => item.track?.kind === "audio");
    if (sender?.track) {
      sender.track.enabled = false;
    }
  };

  manager.enableVideo = (): void => {
    const sender = peerConnection.getSenders().find((item) => item.track?.kind === "video");
    if (sender?.track) {
      sender.track.enabled = true;
    }
  };

  manager.disableVideo = (): void => {
    const sender = peerConnection.getSenders().find((item) => item.track?.kind === "video");
    if (sender?.track) {
      sender.track.enabled = false;
    }
  };

  manager.writeBytes = (input: Uint8Array | ArrayBuffer): void => {
    if (!dataChannel || dataChannel.readyState !== "open") {
      throw new Error("RTC data channel is not open.");
    }

    const payload = input instanceof Uint8Array ? input : new Uint8Array(input);
    dataChannel.send(payload as any);
  };

  manager.subscribeBytes = (callback: ByteSubscriber | RtcMgrDotNetReference): number => {
    const subscriber: ByteSubscriber = typeof callback === "function"
      ? callback
      : (data) => {
        void callback.invokeMethodAsync("OnBytesReceived", data);
      };

    nextSubscriberId += 1;
    const id = nextSubscriberId;
    subscribers.set(id, subscriber);
    return id;
  };

  manager.unsubscribeBytes = (subscriptionId: number): void => {
    subscribers.delete(subscriptionId);
  };

  manager.getLocalAnswer = (): RTCSessionDescription | null => localAnswer;

  manager.close = (): void => {
    if (isClosed) {
      return;
    }

    isClosed = true;
    if (dataChannel) {
      dataChannel.close();
      dataChannel = null;
    }

    peerConnection.getSenders().forEach((sender) => {
      if (sender.track) {
        sender.track.stop();
      }
    });

    peerConnection.close();
    subscribers.clear();
  };

  peerConnection.onconnectionstatechange = () => {
    void stateCallback.invokeMethodAsync("OnStateChanged", peerConnection.connectionState);
  };

  peerConnection.ondatachannel = (event) => {
    wireDataChannel(event.channel);
  };

  return {
    manager,
    peerConnection,
    wireDataChannel,
    setLocalAnswer: (description: RTCSessionDescription | null): void => {
      localAnswer = description;
    }
  };
}

async function initiateConnectionAsync(
  dotnetCallback: RtcMgrDotNetReference,
  onStateChanged: RtcMgrDotNetReference
): Promise<RtcConnectionManager> {
  const created = createRtcManagerConnection(onStateChanged);
  const channel = created.peerConnection.createDataChannel("primary", { ordered: true });
  created.wireDataChannel(channel);

  const offer = await created.peerConnection.createOffer();
  await created.peerConnection.setLocalDescription(offer);
  await waitForIceGatheringCompleteRtcMgr(created.peerConnection);

  const answer = await dotnetCallback.invokeMethodAsync<RTCSessionDescriptionInit>(
    "AcceptOfferAsync",
    created.peerConnection.localDescription
  );

  await created.peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
  return created.manager;
}

async function acceptConnectionAsync(
  offer: RTCSessionDescriptionInit,
  onStateChanged: RtcMgrDotNetReference
): Promise<RtcConnectionManager> {
  const created = createRtcManagerConnection(onStateChanged);
  await created.peerConnection.setRemoteDescription(new RTCSessionDescription(offer));

  const answer = await created.peerConnection.createAnswer();
  await created.peerConnection.setLocalDescription(answer);
  await waitForIceGatheringCompleteRtcMgr(created.peerConnection);

  created.setLocalAnswer(created.peerConnection.localDescription);
  return created.manager;
}

window.rtcConnectionManagerInterop = {
  initiateConnectionAsync,
  acceptConnectionAsync
};