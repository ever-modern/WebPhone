System.register("ice-utilities", [], function (exports_1, context_1) {
    "use strict";
    var __moduleName = context_1 && context_1.id;
    function waitForIceGatheringComplete(peerConnection, timeoutMs = 2000) {
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
    exports_1("waitForIceGatheringComplete", waitForIceGatheringComplete);
    return {
        setters: [],
        execute: function () {
        }
    };
});
System.register("rtc-connection", [], function (exports_2, context_2) {
    "use strict";
    var __moduleName = context_2 && context_2.id;
    function createEventSource() {
        const callbacks = [];
        return {
            subscribe: (callback) => {
                callbacks.push(callback);
                return {
                    finish: () => {
                        const index = callbacks.indexOf(callback);
                        if (index !== -1) {
                            callbacks.splice(index, 1);
                        }
                    }
                };
            },
            invoke: async (event) => {
                for (const callback of callbacks) {
                    try {
                        await callback(event);
                    }
                    catch (error) {
                        console.error("Error invoking callback:", error);
                    }
                }
            }
        };
    }
    exports_2("createEventSource", createEventSource);
    return {
        setters: [],
        execute: function () {
        }
    };
});
System.register("rtc-connection-factory", ["ice-utilities"], function (exports_3, context_3) {
    "use strict";
    var ice_utilities_1, rtcConnectionFactory;
    var __moduleName = context_3 && context_3.id;
    function bindCallbacks(connection, { onStateChanged, onDataChannelMessage }) {
        connection.ondatachannel = (event) => {
            const channel = event.channel;
            if (!channel) {
                return;
            }
            channel.onmessage = (event) => {
                onDataChannelMessage?.(event.data);
            };
        };
        connection.onconnectionstatechange = () => {
            onStateChanged?.(connection.connectionState);
        };
        return () => { connection.ondatachannel = null, connection.onconnectionstatechange = null; };
    }
    async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
        if (!iceServers?.length)
            throw new Error("At least one ICE server must be provided.");
        const peerConnection = new RTCPeerConnection({ iceServers });
        const unbindCallbacks = bindCallbacks(peerConnection, callbacks);
        const agent = {
            close: () => {
                unbindCallbacks();
                peerConnection.close();
            }, getState: () => peerConnection.connectionState
        };
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
        const audioElement = createAudioElement();
        stream.getTracks().forEach((track) => peerConnection.addTrack(track, stream));
        if (typeof exchangeInfo === "function") {
            peerConnection.createDataChannel("data");
            const offer = await peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);
            await ice_utilities_1.waitForIceGatheringComplete(peerConnection);
            const answer = await exchangeInfo(offer);
            await peerConnection.setRemoteDescription(answer);
        }
        else {
            const { offer, sendAnswerBack } = exchangeInfo;
            await peerConnection.setRemoteDescription(offer);
            const answer = await peerConnection.createAnswer();
            await peerConnection.setLocalDescription(answer);
            await ice_utilities_1.waitForIceGatheringComplete(peerConnection);
            await sendAnswerBack(answer);
        }
        peerConnection.ontrack = (event) => {
            const stream = event.streams[0];
            if (stream) {
                audioElement.srcObject = stream;
                audioElement.play()?.catch(() => { });
                const localStream = stream;
            }
        };
        return agent;
    }
    function createAudioElement() {
        const remoteAudioElement = document.createElement("audio");
        remoteAudioElement.autoplay = true;
        remoteAudioElement.setAttribute("playsinline", "true");
        remoteAudioElement.style.display = "none";
        document.body.appendChild(remoteAudioElement);
        return remoteAudioElement;
    }
    async function initiateConnectionAsync(iceServers, getAnswerAsync, onStateChangedAsync, onDataChannelMessageAsync) {
        const getAnswer = (offer) => getAnswerAsync.invokeMethodAsync("invoke", offer);
        const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
        const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
        const result = await createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });
        return result;
    }
    async function acceptConnectionAsync(iceServers, offer, sendAnswerBackAsync, onStateChangedAsync, onDataChannelMessageAsync) {
        const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
        const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
        const sendAnswerBack = (answer) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer);
        const result = await createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });
        return result;
    }
    return {
        setters: [
            function (ice_utilities_1_1) {
                ice_utilities_1 = ice_utilities_1_1;
            }
        ],
        execute: function () {
            rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };
            window.rtcConnectionFactory = rtcConnectionFactory;
        }
    };
});
