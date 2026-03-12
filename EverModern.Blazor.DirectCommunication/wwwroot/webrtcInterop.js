const connections = new Map();
const dotNetReferences = new Map();
const dataChannels = new Map();
const localStreams = new Map();

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
  channel.onmessage = (event) => {
    dotNetReference.invokeMethodAsync("OnDataChannelMessage", id, event.data);
  };

  channel.onopen = () => {
    dotNetReference.invokeMethodAsync("OnDataChannelStateChanged", id, channel.readyState);
  };

  channel.onclose = () => {
    dotNetReference.invokeMethodAsync("OnDataChannelStateChanged", id, channel.readyState);
  };
}

function waitForIceGatheringComplete(peerConnection) {
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

    return {
      urls: server.urls,
      username: server.username,
      credential: server.credential
    };
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

  peerConnection.onicecandidate = (event) => {
    if (event.candidate) {
      dotNetReference.invokeMethodAsync("OnIceCandidate", id, event.candidate);
    }
  };

  peerConnection.onconnectionstatechange = () => {
    dotNetReference.invokeMethodAsync("OnConnectionStateChanged", id, peerConnection.connectionState);
  };

  peerConnection.ontrack = (event) => {
    const stream = event.streams[0];
    if (stream) {
      dotNetReference.invokeMethodAsync("OnRemoteStream", id);
    }
  };

  peerConnection.ondatachannel = (event) => {
    wireDataChannel(id, event.channel);
  };

  connections.set(id, peerConnection);
  dotNetReferences.set(id, dotNetReference);
}

async function startLocalStream(id, constraints) {
  if (localStreams.has(id)) {
    return localStreams.get(id);
  }

  const resolvedConstraints = constraints ?? { audio: true, video: false };
  const stream = await navigator.mediaDevices.getUserMedia(resolvedConstraints);
  localStreams.set(id, stream);
  return stream;
}

function addLocalTracks(id) {
  const connection = getConnection(id);
  const stream = localStreams.get(id);
  if (!stream) {
    throw new Error(`No local stream found for id '${id}'.`);
  }

  stream.getTracks().forEach((track) => connection.addTrack(track, stream));
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

function sendData(id, message) {
  const channel = getDataChannel(id);
  if (channel.readyState !== "open") {
    throw new Error(`RTCDataChannel for id '${id}' is not open.`);
  }

  channel.send(message);
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

  stopLocalStream(id);
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
  closeConnection
};
