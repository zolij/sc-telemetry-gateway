"use strict";

const $ = id => document.getElementById(id);
const pairing = $("pairing");
const dashboard = $("dashboard");
const pairMessage = $("pair-message");
const pairButton = $("pair-button");
const views = {
  flight: [
    ["vehicle.state.flight.instruments.speed", "Speed", true],
    ["vehicle.state.flight.instruments.altitude", "Altitude"],
    ["vehicle.state.flight.instruments.heading", "Heading"],
    ["vehicle.state.flight.instruments.hydrogen_fuel", "Hydrogen fuel"],
    ["vehicle.state.flight.instruments.quantum_fuel", "Quantum fuel"],
    ["vehicle.state.flight.instruments.radar_altitude", "Radar altitude"],
    ["vehicle.state.flight.instruments.vertical_speed_indicator", "Vertical speed"]
  ],
  systems: [
    ["vehicle.state.shields.level", "Shield level", true],
    ["vehicle.state.health", "Vehicle health"],
    ["vehicle.state.master_mode.mode", "Master mode"],
    ["vehicle.state.flight.state.gear", "Landing gear"],
    ["vehicle.state.flight.state.is_landed", "Landed"],
    ["vehicle.state.flight.state.vtol", "VTOL"],
    ["vehicle.state.doors.lock_state", "Door lock"]
  ],
  crew: [
    ["actor.vitals.heart_rate", "Heart rate", true],
    ["actor.vitals.health.current", "Health"],
    ["actor.vitals.oxygen.current", "Oxygen"],
    ["actor.vitals.hunger.current", "Hunger"],
    ["actor.vitals.thirst.current", "Thirst"],
    ["actor.state.locomotion", "Locomotion"],
    ["actor.environment.breathable_atmosphere", "Breathable atmosphere"]
  ]
};
const primaryPaths = new Set(Object.values(views).flat().map(item => item[0]));
const cards = new Map();
let extraCards = new Map();
let extraSignature = null;
let snapshot = null;
let receivedAt = 0;
let socket = null;
let reconnectTimer = null;
let token = sessionStorage.getItem("mfd-session-token");

function node(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function makeCard(path, label, primary = false, extra = false) {
  const card = node("article", extra ? "extra-item" : "instrument" + (primary ? " primary" : ""));
  const title = node("span", "instrument-label", label);
  const value = node("strong", "instrument-value", "Unavailable");
  const meta = node("span", "instrument-meta", "Waiting for telemetry");
  card.append(title, value, meta);
  return { path, card, value, meta };
}

for (const [view, definitions] of Object.entries(views)) {
  for (const [path, label, primary] of definitions) {
    const card = makeCard(path, label, primary);
    cards.set(path, card);
    $(view + "-cards").append(card.card);
  }
}

function readable(text) {
  return text.replace(/^(VEHICLE_COMPONENT_DEPLOYMENT_STATE|VEHICLE_MASTER_MODE|ACTOR_LOCOMOTION_STATE|ACTOR_CREW_ROLE|DOOR_OPEN_STATE|DOOR_LOCK_STATE|ANNUNCIATOR_STATE|VEHICLE_TYPE|COUNTERMEASURE_TYPE)_/, "")
    .replace(/_/g, " ").toLowerCase().replace(/\b\w/g, letter => letter.toUpperCase());
}

function fieldLabel(path) {
  return path.split(".").filter(part => !["state", "flight", "instruments", "actor", "vehicle"].includes(part))
    .map(readable).join(" / ");
}

function formatValue(value) {
  if (value === null || value === undefined) return "Unavailable";
  if (Array.isArray(value)) return value.length ? value.map(formatValue).join(" · ") : "None";
  if (typeof value === "number") return Number.isFinite(value) ? new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value) : "Unavailable";
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (typeof value === "string") return value ? (/^[A-Z][A-Z0-9_]*$/.test(value) ? readable(value) : value) : "Unavailable";
  return "Unavailable";
}

function viewFor(path) {
  if (path.startsWith("actor.")) return "crew";
  if (path.startsWith("vehicle.state.flight.instruments.")) return "flight";
  return "systems";
}

function refreshExtras(selected) {
  const paths = [...selected].filter(path => !primaryPaths.has(path)).sort();
  const signature = paths.join("\n");
  if (signature === extraSignature) return;
  extraSignature = signature;
  extraCards = new Map();
  for (const view of Object.keys(views)) $(view + "-extra").replaceChildren();
  for (const path of paths) {
    const card = makeCard(path, fieldLabel(path), false, true);
    extraCards.set(path, card);
    $(viewFor(path) + "-extra").append(card.card);
  }
  for (const view of Object.keys(views)) {
    const container = $(view + "-extra");
    if (!container.childElementCount) container.append(node("p", "extra-empty", "No additional fields selected for this view."));
  }
}

function ageText(milliseconds) {
  if (!Number.isFinite(milliseconds)) return "Age unavailable";
  if (milliseconds < 1000) return "<1 s ago";
  return Math.floor(milliseconds / 1000) + " s ago";
}

function paintCard(card, selected, online, elapsed) {
  const field = snapshot?.fields?.[card.path];
  if (!selected.has(card.path)) {
    card.value.textContent = "Not selected";
    card.meta.textContent = "Select in gateway settings";
    card.card.dataset.state = "unavailable";
    return;
  }
  if (!field) {
    card.value.textContent = "Unavailable";
    card.meta.textContent = online ? "No value received" : "Offline";
    card.card.dataset.state = "unavailable";
    return;
  }
  const age = Number.isFinite(field.age_ms) ? field.age_ms + elapsed : NaN;
  card.value.textContent = formatValue(field.value);
  const label = !online ? "OFFLINE · LAST KNOWN" : field.last_known ? "LAST KNOWN" : field.value === null ? "UNAVAILABLE" :
    !Number.isFinite(age) ? "AGE UNKNOWN" : age >= 2000 ? "STALE" : "LIVE";
  card.meta.textContent = label + " · " + ageText(age);
  card.card.dataset.state = !online ? "offline" : field.last_known ? "last-known" :
    !Number.isFinite(age) || age >= 2000 ? "stale" : "live";
}

function paint() {
  if (dashboard.hidden) return;
  const elapsed = snapshot ? Math.max(0, performance.now() - receivedAt) : 0;
  const connection = $("connection");
  const online = socket?.readyState === WebSocket.OPEN && snapshot?.connection_status === "connected" && elapsed < 3000;
  const frameAge = Number.isFinite(snapshot?.last_frame_age_ms) ? snapshot.last_frame_age_ms + elapsed : NaN;
  const status = !snapshot ? "WAITING FOR DATA" : !online ?
    (snapshot.connection_status === "paused" && socket?.readyState === WebSocket.OPEN ? "PAUSED" : "OFFLINE") :
    !Number.isFinite(frameAge) ? "WAITING FOR GAME" : frameAge >= 2000 ? "STALE" : "LIVE";
  connection.textContent = status;
  connection.parentElement.className = "link-status " + (status === "LIVE" ? "live" : status === "OFFLINE" || status === "PAUSED" ? "offline" : "");
  $("frame-age").textContent = Number.isFinite(frameAge) ? "FRAME " + ageText(frameAge).toUpperCase() : "NO FRAME";
  const selected = new Set(snapshot?.selected_fields ?? Object.keys(snapshot?.fields ?? {}));
  refreshExtras(selected);
  for (const card of cards.values()) paintCard(card, selected, online, elapsed);
  for (const card of extraCards.values()) paintCard(card, selected, online, elapsed);
}

function showView(view) {
  for (const name of Object.keys(views)) $("view-" + name).hidden = name !== view;
  for (const button of document.querySelectorAll(".nav-button")) {
    const active = button.dataset.view === view;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  }
  $("view-kicker").textContent = ({ flight: "01 / FLIGHT", systems: "02 / SYSTEMS", crew: "03 / CREW" })[view];
}

for (const button of document.querySelectorAll(".nav-button"))
  button.addEventListener("click", () => showView(button.dataset.view));

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
  pairMessage.textContent = "Waiting for approval on the PC…";
  try {
    const response = await fetch("/api/pair/request", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
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
  } catch (error) {
    pairMessage.textContent = error instanceof Error ? error.message : "Pairing failed.";
  } finally {
    pairButton.disabled = false;
  }
});

$("unpair-button").addEventListener("click", () => {
  token = null;
  sessionStorage.removeItem("mfd-session-token");
  clearTimeout(reconnectTimer);
  socket?.close();
  socket = null;
  snapshot = null;
  dashboard.hidden = true;
  pairing.hidden = false;
  pairMessage.textContent = "Request a new code from the PC.";
  $("connection").textContent = "NOT PAIRED";
  $("connection").parentElement.className = "link-status";
});

setInterval(paint, 250);
if (token) {
  pairing.hidden = true;
  dashboard.hidden = false;
  connect();
}
paint();
