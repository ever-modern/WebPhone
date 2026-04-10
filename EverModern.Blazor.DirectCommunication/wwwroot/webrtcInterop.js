const connections = new Map();
const dotNetReferences = new Map();
const dataChannels = new Map();
const localStreams = new Map();
const remoteStreams = new Map();
const pendingRemoteAudioElements = new Map();

function getConnection(id) {
  const connection = connections.get(id);
  if (!connection) {
    throw new Error(`No RTCPeerConnection found for id '${id}'.`);
  }
  return connection;
}

function getDataChannel(id) {
  const channel = dataChannels.get(id);
  if (!channel) {
    throw new Error(`No RTCDataChannel found for id '${id}'.`);
  }
  return channel;
}

function wireDataChannel(id, channel) {
  const dotNetReference = dotNetReferences.get(id);
  if (!dotNetReference) {
    return;
  }

  dataChannels.set(id, channel);
  channel.binaryType = "arraybuffer";
  channel.onmessage = (event) => {
    const data = event.data;
    if (typeof data === "string") {
      dotNetReference.invokeMethodAsync("OnDataChannelMessage", id, data);
      return;
    }

    if (data instanceof ArrayBuffer) {
      dotNetReference.invokeMethodAsync("OnDataChannelBytesMessage", id, new Uint8Array(data));
      return;
    }

    if (data instanceof Blob) {
      data.arrayBuffer().then((buffer) => {
        dotNetReference.invokeMethodAsync("OnDataChannelBytesMessage", id, new Uint8Array(buffer));
      });
    }
  };

  channel.onopen = () => {
    dotNetReference.invokeMethodAsync("OnDataChannelStateChanged", id, channel.readyState);
  };

  channel.onclose = () => {
    dotNetReference.invokeMethodAsync("OnDataChannelStateChanged", id, channel.readyState);
  };
}

function waitForIceGatheringComplete(peerConnection, timeoutMs = 5000) {
  if (peerConnection.iceGatheringState === "complete") {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    const handler = () => {
      if (peerConnection.iceGatheringState === "complete") {
        peerConnection.removeEventListener("icegatheringstatechange", handler);
        resolve();
      }
    };

    peerConnection.addEventListener("icegatheringstatechange", handler);
    setTimeout(() => {
      peerConnection.removeEventListener("icegatheringstatechange", handler);
      resolve();
    }, timeoutMs);
  });
}

function buildIceServers(iceServers) {
  if (!iceServers || !Array.isArray(iceServers) || iceServers.length === 0) {
    return undefined;
  }

  return iceServers.map((server) => {
    if (typeof server === "string") {
      return { urls: server };
    }

    // Blazor serialises string[] as a JSON array; RTCPeerConnection accepts
    // either a string or an array of strings, but some browsers are stricter,
    // so normalise a single-element array down to a plain string.
    const rawUrls = server.urls ?? server.Urls;
    const urls = Array.isArray(rawUrls) && rawUrls.length === 1 ? rawUrls[0] : rawUrls;

    const entry = { urls };
    if (server.username ?? server.Username) entry.username = server.username ?? server.Username;
    if (server.credential ?? server.Credential) entry.credential = server.credential ?? server.Credential;
    return entry;
  });
}

async function createConnection(id, dotNetReference, iceServers) {
  if (connections.has(id)) {
    dotNetReferences.set(id, dotNetReference);
    return;
  }

  const configuration = {};
  const mappedServers = buildIceServers(iceServers);
  if (mappedServers) {
    configuration.iceServers = mappedServers;
  }

  const peerConnection = new RTCPeerConnection(configuration);
  peerConnection.addTransceiver("audio", { direction: "sendrecv" });

  peerConnection.onicecandidate = (event) => {
    if (event.candidate) {
      dotNetReference.invokeMethodAsync("OnIceCandidate", id, event.candidate);
    }
  };

  peerConnection.onconnectionstatechange = () => {
    dotNetReference.invokeMethodAsync("OnConnectionStateChanged", id, peerConnection.connectionState);
  };

  peerConnection.ontrack = (event) => {
    const streamFromEvent = event.streams && event.streams.length > 0
      ? event.streams[0]
      : null;

    const stream = streamFromEvent ?? remoteStreams.get(id) ?? new MediaStream();
    if (!streamFromEvent) {
      stream.addTrack(event.track);
    }

    remoteStreams.set(id, stream);

    const pendingElement = pendingRemoteAudioElements.get(id);
    if (pendingElement) {
      pendingElement.srcObject = stream;
      pendingElement.muted = false;
      pendingElement.volume = 1;
      if (typeof pendingElement.play === "function") {
        pendingElement.play().catch(() => { });
      }
      pendingRemoteAudioElements.delete(id);
    }

    dotNetReference.invokeMethodAsync("OnRemoteStream", id);
  };

  peerConnection.ondatachannel = (event) => {
    wireDataChannel(id, event.channel);
  };

  connections.set(id, peerConnection);
  dotNetReferences.set(id, dotNetReference);
}

function attachRemoteStream(id, element) {
  const stream = remoteStreams.get(id);
  if (!stream) {
    if (element) {
      pendingRemoteAudioElements.set(id, element);
      return;
    }

    throw new Error(`No remote stream found for id '${id}'.`);
  }

  if (!element) {
    throw new Error("Remote audio element was not provided.");
  }

  element.srcObject = stream;
  element.muted = false;
  element.volume = 1;
  if (typeof element.play === "function") {
    element.play().catch(() => { });
  }
}

async function startLocalStream(id, constraints) {
  if (localStreams.has(id)) {
    return localStreams.get(id);
  }

  if (!navigator?.mediaDevices?.getUserMedia) {
    throw new Error("Media devices are unavailable. Use HTTPS or localhost and allow microphone access.");
  }

  const resolvedConstraints = constraints ?? { audio: true, video: false };
  const stream = await navigator.mediaDevices.getUserMedia(resolvedConstraints);
  localStreams.set(id, stream);
  return stream;
}

async function addLocalTracks(id) {
  const connection = getConnection(id);
  const stream = localStreams.get(id);
  if (!stream) {
    throw new Error(`No local stream found for id '${id}'.`);
  }

  console.log(`[WebRTC] Adding local tracks for connection ${id}`);
  const addedTracks = [];
  for (const track of stream.getTracks()) {
    console.log(`[WebRTC] Processing track: ${track.kind}, id: ${track.id}, enabled: ${track.enabled}`);

    // Prefer sender that already carries this kind; otherwise take the first empty sender.
    // Do NOT require sender.transport !== null here; during setup it's frequently null,
    // and failing to reuse that sender makes addTrack create a new m-line that needs
    // renegotiation (which we don't do for call start).
    const existingSender =
      connection.getSenders().find((sender) => sender.track?.kind === track.kind)
      ?? connection.getSenders().find((sender) => !sender.track);

    if (existingSender) {
      console.log(`[WebRTC] Replacing track in existing sender for ${track.kind}`);
      try {
        await existingSender.replaceTrack(track);
        console.log(`[WebRTC] Successfully replaced ${track.kind} track`);
      } catch (err) {
        console.error(`[WebRTC] Failed to replace ${track.kind} track:`, err);
      }
      addedTracks.push(track.kind);
    } else {
      console.log(`[WebRTC] Adding new sender for ${track.kind} track`);
      const sender = connection.addTrack(track, stream);
      console.log(`[WebRTC] Added sender:`, sender);
      addedTracks.push(track.kind);
    }
  }

  console.log(`[WebRTC] Processed tracks: ${addedTracks.join(', ')}`);
  console.log(`[WebRTC] Total senders on connection: ${connection.getSenders().length}`);
  connection.getSenders().forEach((sender, index) => {
    console.log(`[WebRTC] Sender ${index}: track=${sender.track?.kind || 'none'}, trackId=${sender.track?.id || 'none'}, enabled=${sender.track?.enabled || 'n/a'}`);
  });
}

function createDataChannel(id, label, options) {
  const connection = getConnection(id);
  const channel = connection.createDataChannel(label, options ?? undefined);
  wireDataChannel(id, channel);
}

async function createOffer(id) {
  const connection = getConnection(id);
  const offer = await connection.createOffer();
  await connection.setLocalDescription(offer);
  await waitForIceGatheringComplete(connection);
  return connection.localDescription;
}

async function createAnswer(id) {
  const connection = getConnection(id);
  const answer = await connection.createAnswer();
  await connection.setLocalDescription(answer);
  await waitForIceGatheringComplete(connection);
  return connection.localDescription;
}

async function setRemoteDescription(id, description) {
  const connection = getConnection(id);
  const rtcDescription = new RTCSessionDescription(description);
  await connection.setRemoteDescription(rtcDescription);
}

async function addIceCandidate(id, candidate) {
  const connection = getConnection(id);
  if (!candidate) {
    return;
  }

  await connection.addIceCandidate(new RTCIceCandidate(candidate));
}

async function waitForDataChannelOpen(channel, timeoutMs = 5000) {
  if (channel.readyState === "open") {
    return;
  }

  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error("RTCDataChannel open timeout."));
    }, timeoutMs);

    const onOpen = () => {
      cleanup();
      resolve();
    };

    const onClose = () => {
      cleanup();
      reject(new Error("RTCDataChannel closed before opening."));
    };

    const cleanup = () => {
      clearTimeout(timeout);
      channel.removeEventListener("open", onOpen);
      channel.removeEventListener("close", onClose);
    };

    channel.addEventListener("open", onOpen);
    channel.addEventListener("close", onClose);
  });
}

async function sendData(id, message) {
  const channel = dataChannels.get(id);
  if (!channel) {
    console.warn(`sendData: no data channel for id '${id}', dropping message.`);
    return;
  }

  try {
    await waitForDataChannelOpen(channel);
  } catch (e) {
    console.warn(`sendData: data channel not ready for id '${id}': ${e.message}`);
    return;
  }

  if (channel.readyState !== "open") {
    console.warn(`sendData: channel not open for id '${id}', dropping message.`);
    return;
  }

  try {
    channel.send(message);
  } catch (e) {
    console.warn(`sendData: send failed for id '${id}': ${e.message}`);
  }
}

function stopLocalStream(id) {
  const stream = localStreams.get(id);
  if (!stream) {
    return;
  }

  stream.getTracks().forEach((track) => track.stop());
  localStreams.delete(id);
}

function closeConnection(id) {
  const channel = dataChannels.get(id);
  if (channel) {
    channel.close();
    dataChannels.delete(id);
  }

  const connection = connections.get(id);
  if (connection) {
    connection.close();
    connections.delete(id);
  }

  dotNetReferences.delete(id);
  remoteStreams.delete(id);
  pendingRemoteAudioElements.delete(id);

  stopLocalStream(id);
}

async function copyToClipboard(text) {
  if (navigator?.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return true;
  }

  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.style.position = "fixed";
  textarea.style.opacity = "0";
  document.body.appendChild(textarea);
  textarea.focus();
  textarea.select();

  let success = false;
  try {
    success = document.execCommand("copy");
  } catch {
    success = false;
  }

  document.body.removeChild(textarea);
  return success;
}

window.webrtcInterop = {
  createConnection,
  startLocalStream,
  addLocalTracks,
  createDataChannel,
  createOffer,
  createAnswer,
  setRemoteDescription,
  addIceCandidate,
  sendData,
  stopLocalStream,
  closeConnection,
  copyToClipboard,
  attachRemoteStream
};
