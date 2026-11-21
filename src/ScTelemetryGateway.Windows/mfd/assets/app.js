"use strict";

const pairing = document.getElementById("pairing");
const dashboard = document.getElementById("dashboard");
const fields = document.getElementById("fields");
const connection = document.getElementById("connection");
const pairMessage = document.getElementById("pair-message");
const pairButton = document.getElementById("pair-button");
let socket;
let lastPublication = null;
let lastFrame = null;

function setConnection(status) {
  connection.className = "status " + status;
  connection.textContent = status.toUpperCase();
}

function displayValue(value) {
  if (value === null || value === undefined) return "Unavailable";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}

function render(snapshot) {
  lastPublication = Date.now();
  lastFrame = snapshot.last_frame_at ? Date.parse(snapshot.last_frame_at) : null;
  const status = snapshot.connection_status;
  setConnection(status === "connected" ? "connected" : status);
  fields.replaceChildren();

  const entries = Object.entries(snapshot.fields || {});
  if (entries.length === 0) {
    const frame = document.createElement("article");
    frame.className = "frame";
    const label = document.createElement("span");
    label.className = "label";
    label.textContent = "No selected values";
    frame.append(label);
    fields.append(frame);
  }

  for (const [path, item] of entries) {
    const frame = document.createElement("article");
    frame.className = "frame";
    const label = document.createElement("span");
    label.className = "label";
    label.textContent = item.last_known ? "Last known" : "Selected value";
    const value = document.createElement("strong");
    value.textContent = displayValue(item.value);
    const pathLabel = document.createElement("span");
    pathLabel.className = "path";
    pathLabel.textContent = path;
    frame.append(label, value, pathLabel);
    fields.append(frame);
  }
}

function connect(token) {
  const wsUrl = "ws://" + window.location.host + "/mfd/ws";
  socket = new WebSocket(wsUrl);
  socket.addEventListener("open", () => {
    socket.send(JSON.stringify({
      type: "mfd.auth",
      schema_version: 1,
      session_token: token
    }));
  });
  socket.addEventListener("message", event => {
    try {
      const snapshot = JSON.parse(event.data);
      if (snapshot.type === "mfd.snapshot" && snapshot.schema_version === 1)
        render(snapshot);
    } catch {
      setConnection("disconnected");
    }
  });
  socket.addEventListener("close", () => {
    setConnection("disconnected");
  });
  socket.addEventListener("error", () => setConnection("disconnected"));
}

pairButton.addEventListener("click", async () => {
  const code = document.getElementById("pair-code").value.trim();
  const label = document.getElementById("device-label").value.trim();
  pairButton.disabled = true;
  pairMessage.textContent = "Waiting for PC approval...";
  try {
    const response = await fetch("/api/pair/request", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        type: "mfd.pair.request",
        schema_version: 1,
        code,
        device_label: label
      })
    });
    if (!response.ok) throw new Error("Pairing was rejected or expired.");
    const result = await response.json();
    sessionStorage.setItem("mfd-session-token", result.session_token);
    pairing.hidden = true;
    dashboard.hidden = false;
    connect(result.session_token);
  } catch (error) {
    pairMessage.textContent = error.message;
  } finally {
    pairButton.disabled = false;
  }
});

setInterval(() => {
  const age = document.getElementById("frame-age");
  if (lastFrame === null) {
    age.textContent = "No frame";
  } else {
    const seconds = Math.max(0, (Date.now() - lastFrame) / 1000);
    age.textContent = seconds < 0.1 ? "<0.1 s" : seconds.toFixed(1) + " s";
  }
  if (lastPublication !== null && Date.now() - lastPublication > 3000)
    setConnection("disconnected");
}, 250);

const existingToken = sessionStorage.getItem("mfd-session-token");
if (existingToken) {
  pairing.hidden = true;
  dashboard.hidden = false;
  connect(existingToken);
}
