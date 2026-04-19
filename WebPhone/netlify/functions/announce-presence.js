const presenceStore = new Map();
const presenceTimeoutMs = 30000;

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

  const { userId, name, timestamp } = payload;
  if (!userId || !name) {
    return {
      statusCode: 400,
      headers: { "Access-Control-Allow-Origin": "*" },
      body: "Missing required fields"
    };
  }

  const now = Date.now();
  presenceStore.set(userId, {
    userId,
    name,
    timestamp: timestamp || new Date(now).toISOString(),
    lastSeen: now
  });

  for (const [key, value] of presenceStore.entries()) {
    if (now - value.lastSeen > presenceTimeoutMs) {
      presenceStore.delete(key);
    }
  }

  const presentUsers = Array.from(presenceStore.values())
    .filter((user) => user.userId !== userId)
    .map(({ userId: id, name: displayName, timestamp: seenAt }) => ({
      userId: id,
      name: displayName,
      timestamp: seenAt
    }));

  return {
    statusCode: 200,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Content-Type": "application/json"
    },
    body: JSON.stringify(presentUsers)
  };
};
