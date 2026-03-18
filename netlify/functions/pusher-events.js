const crypto = require("crypto");

const messageQueue = [];

exports.handler = async (event) => {
  if (event.httpMethod === "OPTIONS") {
    return {
      statusCode: 204,
      headers: {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Headers": "Content-Type",
        "Access-Control-Allow-Methods": "POST, OPTIONS"
      }
    };
  }

  if (event.httpMethod === "GET") {
    const messages = messageQueue.splice(0, messageQueue.length);
    return {
      statusCode: 200,
      headers: {
        "Access-Control-Allow-Origin": "*",
        "Content-Type": "application/json"
      },
      body: JSON.stringify(messages)
    };
  }

  if (event.httpMethod !== "POST") {
    return {
      statusCode: 405,
      headers: { "Access-Control-Allow-Origin": "*" },
      body: "Method Not Allowed"
    };
  }

  let payload;
  try {
    payload = JSON.parse(event.body || "{}");
  } catch {
    return {
      statusCode: 400,
      headers: { "Access-Control-Allow-Origin": "*" },
      body: "Invalid JSON"
    };
  }

  let { appId, key, secret, cluster, channel, eventName, data } = payload;
  if ((!appId || !key || !secret || !cluster || !channel || !eventName) && payload.payload) {
    ({ appId, key, secret, cluster, channel, eventName, data } = payload.payload);
  }

  secret = secret || "685d876fda68e0cfa8de";
  if (!appId || !key || !secret || !cluster || !channel || !eventName) {
    return {
      statusCode: 400,
      headers: { "Access-Control-Allow-Origin": "*" },
      body: "Missing required fields"
    };
  }

  const dataString = typeof data === "string" ? data : JSON.stringify(data ?? {});
  let queuePayload = data;
  if (typeof data === "string") {
    try {
      queuePayload = JSON.parse(data);
    } catch {
      queuePayload = data;
    }
  }
  const body = {
    name: eventName,
    channel,
    data: dataString
  };

  const bodyJson = JSON.stringify(body);
  const bodyMd5 = crypto.createHash("md5").update(bodyJson).digest("hex");
  const timestamp = Math.floor(Date.now() / 1000);
  const query = `auth_key=${key}&auth_timestamp=${timestamp}&auth_version=1.0&body_md5=${bodyMd5}`;
  const stringToSign = `POST\n/apps/${appId}/events\n${query}`;
  const signature = crypto.createHmac("sha256", secret).update(stringToSign).digest("hex");
  const url = `https://api-${cluster}.pusher.com/apps/${appId}/events?${query}&auth_signature=${signature}`;

  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: bodyJson
  });

  if (response.ok) {
    messageQueue.push({ type: eventName, payload: queuePayload ?? {} });
  }

  const responseText = await response.text();
  return {
    statusCode: response.status,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Content-Type": "application/json"
    },
    body: responseText || ""
  };
};
