"use strict";

const $ = id => document.getElementById(id);
const pairing = $("pairing");
const dashboard = $("dashboard");
const pairMessage = $("pair-message");
const pairButton = $("pair-button");
let snapshot = null;
let receivedAt = 0;
let socket = null;
let reconnectTimer = null;
let token = sessionStorage.getItem("mfd-session-token");

function ageText(milliseconds) {
  if (!Number.isFinite(milliseconds)) return "Age unavailable";
  if (milliseconds < 1000) return "<1 s ago";
  return Math.floor(milliseconds / 1000) + " s ago";
}

function paint() {
  if (dashboard.hidden) return;
  const elapsed = snapshot ? Math.max(0, performance.now() - receivedAt) : 0;
  const online = socket?.readyState === WebSocket.OPEN && snapshot?.connection_status === "connected" && elapsed < 3000;
  const frameAge = Number.isFinite(snapshot?.last_frame_age_ms) ? snapshot.last_frame_age_ms + elapsed : NaN;
  const status = !snapshot ? "WAITING FOR DATA" : !online ?
    (snapshot.connection_status === "paused" && socket?.readyState === WebSocket.OPEN ? "PAUSED" : "OFFLINE") :
    !Number.isFinite(frameAge) ? "WAITING FOR GAME" : frameAge >= 2000 ? "STALE" : "LIVE";
  $("connection").textContent = status;
  $("connection").parentElement.className = "link-status " + (status === "LIVE" ? "live" : status === "OFFLINE" || status === "PAUSED" ? "offline" : "");
  $("frame-age").textContent = Number.isFinite(frameAge) ? "FRAME " + ageText(frameAge).toUpperCase() : "NO FRAME";
  MfdWorkspace.paint(snapshot, online, elapsed, ageText);
}

function connect() {
  if (!token) return;
  clearTimeout(reconnectTimer);
  const current = new WebSocket("ws://" + window.location.host + "/mfd/ws");
  socket = current;
  current.addEventListener("open", () => current.send(JSON.stringify({
    type: "mfd.auth", schema_version: 1, session_token: token
  })));
  current.addEventListener("message", event => {
    try {
      const message = JSON.parse(event.data);
      if (message.type !== "mfd.snapshot" || message.schema_version !== 1) throw new Error("Unsupported snapshot");
      snapshot = message;
      receivedAt = performance.now();
      paint();
    } catch {
      current.close(1008, "Invalid snapshot");
    }
  });
  current.addEventListener("close", () => {
    if (socket !== current) return;
    paint();
    if (token) reconnectTimer = setTimeout(connect, 3000);
  });
}

$("pair-form").addEventListener("submit", async event => {
  event.preventDefault();
  pairButton.disabled = true;
  pairMessage.textContent = "Waiting for approval on the PC...";
  try {
    const response = await fetch("/api/pair/request", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        type: "mfd.pair.request", schema_version: 1,
        code: $("pair-code").value.trim(), device_label: $("device-label").value.trim()
      })
    });
    if (!response.ok) throw new Error("Pairing was rejected or expired. Request a new code.");
    const result = await response.json();
    if (typeof result.session_token !== "string" || !result.session_token) throw new Error("Invalid pairing response.");
    token = result.session_token;
    sessionStorage.setItem("mfd-session-token", token);
    $("pair-code").value = "";
    pairing.hidden = true;
    dashboard.hidden = false;
    connect();
    MfdWorkspace.start();
  } catch (error) {
    pairMessage.textContent = error instanceof Error ? error.message : "Pairing failed.";
  } finally {
    pairButton.disabled = false;
  }
});

$("unpair-button").addEventListener("click", () => {
  if (!MfdWorkspace.canLeave()) return;
  token = null;
  sessionStorage.removeItem("mfd-session-token");
  clearTimeout(reconnectTimer);
  socket?.close();
  socket = null;
  snapshot = null;
  MfdWorkspace.reset();
  dashboard.hidden = true;
  pairing.hidden = false;
  pairMessage.textContent = "Request a new code from the PC.";
  $("connection").textContent = "NOT PAIRED";
  $("connection").parentElement.className = "link-status";
});

MfdWorkspace.init(() => token, paint);
setInterval(paint, 250);
if (token) {
  pairing.hidden = true;
  dashboard.hidden = false;
  connect();
  MfdWorkspace.start();
}
paint();
