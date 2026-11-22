const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "../../src/ScTelemetryGateway.Windows/mfd/assets/app.js"), "utf8");

function pairingFixture() {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      hidden: id === "dashboard",
      value: "",
      textContent: "",
      disabled: false,
      className: "",
      parentElement: { className: "" },
      addEventListener() {}
    });
    return elements.get(id);
  };
  const values = new Map([["mfd-session-token", "revoked-token"]]);
  const requests = [];
  const sockets = [];
  let resolveResponse;
  const response = new Promise(resolve => { resolveResponse = resolve; });
  let replacedUrl;
  class Socket {
    constructor(url) {
      this.url = url;
      this.readyState = 0;
      this.listeners = {};
      sockets.push(this);
    }
    addEventListener(type, callback) { this.listeners[type] = callback; }
    send() {}
    close() {}
  }
  Socket.OPEN = 1;
  const context = {
    document: { getElementById: element },
    localStorage: {
      getItem: key => values.get(key) ?? null,
      setItem: (key, value) => values.set(key, value),
      removeItem: key => values.delete(key)
    },
    window: { location: { hash: "#pair=123456", pathname: "/", search: "", host: "gateway:8080" } },
    history: { replaceState(_state, _title, url) { replacedUrl = url; } },
    MfdFocus: { init() {} },
    MfdWorkspace: {
      init() {}, start() {}, paint() {}, resetEvents() {}, receive() {}, canLeave: () => true, reset() {}
    },
    MfdDeviceIdentity: { getOrCreate: () => "123e4567-e89b-42d3-a456-426614174000" },
    WebSocket: Socket,
    performance: { now: () => 0 },
    URLSearchParams,
    fetch: (url, options) => {
      requests.push({ url, options });
      return response;
    },
    setInterval() {},
    setTimeout,
    clearTimeout
  };
  vm.runInNewContext(source, context);
  return {
    context, element, values, requests, sockets, response,
    resolveResponse: resolve => resolveResponse(resolve),
    replacedUrl: () => replacedUrl
  };
}

test("QR pairing replaces a saved revoked session instead of reconnecting with it", async () => {
  const view = pairingFixture();
  assert.equal(view.values.has("mfd-session-token"), false);
  assert.equal(view.replacedUrl(), "/");
  assert.equal(view.requests.length, 1);
  assert.equal(view.requests[0].url, "/api/pair/request");
  assert.equal(JSON.parse(view.requests[0].options.body).code, "123456");
  assert.equal(view.sockets.length, 0);

  view.resolveResponse({
    status: 200,
    ok: true,
    json: async () => ({ session_token: "new-token" })
  });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(view.values.get("mfd-session-token"), "new-token");
  assert.equal(view.sockets.length, 1);
  assert.equal(view.element("pairing").hidden, true);
  assert.equal(view.element("dashboard").hidden, false);
});
