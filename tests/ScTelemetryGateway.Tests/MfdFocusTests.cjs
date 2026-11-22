const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../../src/ScTelemetryGateway.Windows/mfd/assets/focus.js"), "utf8");

function fixture({ supported = true, prefixed = false, standalone = false, request, exit } = {}) {
  const elements = new Map();
  const listeners = new Map();
  const classes = new Set();
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      hidden: false, disabled: false, textContent: "", attributes: {}, listeners: {},
      setAttribute(name, value) { this.attributes[name] = value; },
      addEventListener(type, callback) { this.listeners[type] = callback; },
      focus() { doc.activeElement = this; }
    });
    return elements.get(id);
  };
  const doc = {
    documentElement: {}, fullscreenElement: null,
    body: { classList: { toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); } } },
    getElementById: element,
    addEventListener(type, callback) { listeners.set(type, callback); }
  };
  let requests = 0, exits = 0, wakeStarts = 0, wakeStops = 0, wakeWarning;
  const change = value => {
    doc[prefixed ? "webkitFullscreenElement" : "fullscreenElement"] = value;
    listeners.get(prefixed ? "webkitfullscreenchange" : "fullscreenchange")?.();
  };
  if (supported) {
    doc.documentElement[prefixed ? "webkitRequestFullscreen" : "requestFullscreen"] = function (options) {
      requests++;
      assert.equal(this, doc.documentElement);
      assert.equal(options.navigationUI, "hide");
      if (request) return request(change, doc.documentElement);
      change(doc.documentElement);
      return prefixed ? undefined : Promise.resolve();
    };
    doc[prefixed ? "webkitExitFullscreen" : "exitFullscreen"] = function () {
      exits++;
      assert.equal(this, doc);
      if (exit) return exit();
      change(null);
      return Promise.resolve();
    };
  }
  const focus = vm.runInNewContext(source + "\nMfdFocus;", {
    document: doc, navigator: { standalone },
    window: { matchMedia: () => ({ matches: false }) },
    MfdWakeLock: {
      init(callback) { wakeWarning = callback; },
      start() { wakeStarts++; },
      stop() { wakeStops++; }
    }
  });
  focus.init();
  focus.update(true, true);
  return { focus, element, change, doc, classes, listeners,
    enter: () => element("focus-button").listeners.click(),
    leave: () => element("leave-focus").listeners.click(),
    focused: () => classes.has("focus-mode"),
    requests: () => requests, exits: () => exits,
    wakeStarts: () => wakeStarts, wakeStops: () => wakeStops, wakeWarning: text => wakeWarning(text) };
}

test("focus requests the whole document synchronously and touch exit restores the normal view", async () => {
  const view = fixture();
  const entering = view.enter();
  assert.equal(view.requests(), 1);
  assert.equal(view.wakeStarts(), 1);
  assert.equal(view.focused(), true);
  await entering;
  await view.leave();
  assert.equal(view.focused(), false);
  assert.equal(view.exits(), 1);
  assert.equal(view.wakeStops(), 1);
  assert.equal(view.doc.activeElement, view.element("focus-button"));
});

test("native fullscreen exit and Escape both restore focus controls", async () => {
  const view = fixture();
  await view.enter();
  view.change(null);
  assert.equal(view.focused(), false);
  assert.equal(view.element("leave-focus").hidden, true);
  await view.enter();
  view.listeners.get("keydown")({ key: "Escape" });
  assert.equal(view.focused(), false);
  assert.equal(view.exits(), 1);
});

test("unsupported and rejected fullscreen keep the minimal view and explicitly report the limitation", async () => {
  const unsupported = fixture({ supported: false });
  await unsupported.enter();
  assert.equal(unsupported.focused(), true);
  assert.match(unsupported.element("focus-message").textContent, /Home Screen/);
  unsupported.element("dismiss-focus-message").listeners.click();
  assert.equal(unsupported.element("focus-feedback").hidden, true);
  await unsupported.leave();
  assert.equal(unsupported.focused(), false);

  const rejected = fixture({ request: () => Promise.reject(new Error("denied")) });
  await rejected.enter();
  assert.equal(rejected.focused(), true);
  assert.match(rejected.element("focus-message").textContent, /refused/);
  assert.equal(rejected.element("focus-button").disabled, false);
});

test("Home Screen mode needs no fullscreen request and prefixed Safari APIs are supported", async () => {
  const home = fixture({ standalone: true });
  await home.enter();
  assert.equal(home.focused(), true);
  assert.equal(home.requests(), 0);
  assert.equal(home.element("focus-feedback").hidden, true);
  const safari = fixture({ prefixed: true });
  await safari.enter();
  assert.equal(safari.requests(), 1);
  await safari.leave();
  assert.equal(safari.exits(), 1);
});

test("editing or losing the saved profile leaves focus, but transient busy state does not", async () => {
  const view = fixture();
  await view.enter();
  view.focus.update(false, true);
  assert.equal(view.focused(), true);
  view.focus.update(false, false);
  assert.equal(view.focused(), false);
  assert.equal(view.exits(), 1);
  await view.enter();
  assert.equal(view.requests(), 1);
});

test("leaving while permission is pending cannot strand the page in fullscreen or start overlapping requests", async () => {
  let grant;
  const view = fixture({ request: (change, root) => new Promise(resolve => {
    grant = () => { change(root); resolve(); };
  }) });
  const entering = view.enter();
  await view.enter();
  assert.equal(view.requests(), 1);
  await view.leave();
  grant();
  await entering;
  assert.equal(view.focused(), false);
  assert.equal(view.doc.fullscreenElement, null);
  assert.equal(view.exits(), 1);

  let lateGrant;
  const safari = fixture({ prefixed: true, request: (change, root) => { lateGrant = () => change(root); } });
  await safari.enter();
  await safari.leave();
  lateGrant();
  assert.equal(safari.doc.webkitFullscreenElement, null);
  assert.equal(safari.exits(), 1);
});

test("failed fullscreen exit is visible rather than silently swallowed", async () => {
  const view = fixture({ exit: () => Promise.reject(new Error("denied")) });
  await view.enter();
  await view.leave();
  assert.match(view.element("focus-message").textContent, /Could not leave/);
  assert.equal(view.element("focus-feedback").hidden, false);
});

test("wake lock failures remain visible alongside fullscreen warnings", async () => {
  const view = fixture({ supported: false });
  await view.enter();
  view.wakeWarning("Could not keep the screen awake.");
  assert.match(view.element("focus-message").textContent, /Home Screen/);
  assert.match(view.element("focus-message").textContent, /screen awake/);
  await view.leave();
  assert.equal(view.wakeStops(), 1);
});
