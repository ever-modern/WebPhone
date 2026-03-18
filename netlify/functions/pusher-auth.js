const crypto = require("crypto");

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

  if (event.httpMethod !== "POST") {
    return {
      statusCode: 405,
      headers: { "Access-Control-Allow-Origin": "*" },
      body: "Method Not Allowed"
    };
  }

  let payload = {};
  const rawBody = event.isBase64Encoded ? Buffer.from(event.body || "", "base64").toString("utf8") : (event.body || "");
  const contentType = (event.headers?.["content-type"] || event.headers?.["Content-Type"] || "").toLowerCase();

  if (contentType.includes("application/json")) {
    try {
      payload = JSON.parse(rawBody || "{}");
    } catch {
      return {
        statusCode: 400,
        headers: { "Access-Control-Allow-Origin": "*" },
        body: "Invalid JSON"
      };
    }
  } else {
    const params = new URLSearchParams(rawBody);
    payload = Object.fromEntries(params.entries());
  }

  const { socket_id: socketId, channel_name: channelName, key, secret } = payload;
  if (!socketId || !channelName || !key || !secret) {
    return {
      statusCode: 400,
      headers: { "Access-Control-Allow-Origin": "*" },
      body: "Missing required fields"
    };
  }

  const stringToSign = `${socketId}:${channelName}`;
  const signature = crypto.createHmac("sha256", secret).update(stringToSign).digest("hex");
  const auth = `${key}:${signature}`;

  return {
    statusCode: 200,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ auth })
  };
};
