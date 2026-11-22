const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");

const assets = path.join(__dirname, "../../src/ScTelemetryGateway.Windows/mfd/assets");
const source = ["presentation.js", "triggers.js", "workspace.js"]
  .map(file => fs.readFileSync(path.join(assets, file), "utf8")).join("\n");
const enter = "GAME_EVENT_TYPE_ACTOR_ENTER_SEAT";
const profile = revision => ({
  schema_version: 1, id: "ship", name: "Ship", revision,
  pages: [
    { id: "flight", name: revision === 1 ? "Flight" : "Updated flight", tiles: [], trigger_events: [] },
    { id: "crew", name: revision === 1 ? "Crew" : "Updated crew", tiles: [], trigger_events: revision === 1 ? [] : [enter] }
  ]
});

async function fixture({ preference = { profile: "ship", page: "flight" }, startEmpty = false } = {}) {
  const elements = new Map();
  function element() {
    const queries = new Map();
    return {
      children: [], dataset: {}, value: "", hidden: false, disabled: false, textContent: "", listeners: new Map(),
      classList: { toggle() {} },
      append(...items) { this.children.push(...items); },
      replaceChildren(...items) { this.children = items; },
      get childElementCount() { return this.children.length; },
      setAttribute() {},
      focus() {},
      select() {},
      close() {},
      addEventListener(type, callback) { this.listeners.set(type, callback); },
      click() { return this.listeners.get("click")?.(); },
      querySelector(selector) {
        if (!queries.has(selector)) queries.set(selector, element());
        return queries.get(selector);
      }
    };
  }
  const getElement = id => {
    if (!elements.has(id)) elements.set(id, element());
    return elements.get(id);
  };
  let remote = startEmpty ? null : profile(1), poll, failedProfile = false, blockedProfile = null, blockedBody = null, profileReads = 0;
  const summaryStatuses = [];
  const response = (status, data, etag = null) => ({
    status, ok: status >= 200 && status < 300,
    headers: { get: () => etag },
    json: async () => structuredClone(data)
  });
  const workspace = vm.runInNewContext(source + "\nMfdWorkspace;", {
    document: { getElementById: getElement, createElement: element, querySelector: getElement, querySelectorAll: () => [] },
    window: { addEventListener() {}, confirm: () => true },
    localStorage: { getItem: () => preference ? JSON.stringify(preference) : null, setItem() {} },
    MfdFocus: { update() {} }, AbortSignal, TextEncoder, crypto: webcrypto,
    Option: function (text, value) { return Object.assign(element(), { textContent: text, value }); },
    setInterval: callback => { poll = callback; },
    fetch: async (url, options) => {
      if (url.endsWith("/fields")) return response(200, []);
      if (url.endsWith("/events")) return response(200, [enter]);
      if (url.endsWith("/profiles")) {
        const etag = '"' + (remote?.revision ?? "empty") + '"';
        const status = options.headers["If-None-Match"] === etag ? 304 : 200;
        summaryStatuses.push(status);
        return response(status, remote ? [{ id: remote.id, name: remote.name, revision: remote.revision }] : [], etag);
      }
      assert.equal(url, "/api/mfd/profiles/ship");
      profileReads++;
      if (failedProfile) {
        failedProfile = false;
        return response(503, { message: "Temporary profile read failure" });
      }
      const data = structuredClone(remote);
      if (blockedProfile) {
        const blocked = blockedProfile;
        blockedProfile = null;
        blocked.started();
        await blocked.wait;
      }
      if (blockedBody) {
        const blocked = blockedBody;
        blockedBody = null;
        return { ...response(200, data), json: async () => {
          blocked.started();
          await blocked.wait;
          return data;
        } };
      }
      return response(200, data);
    }
  });
  workspace.init(() => "test-token", () => {});
  await workspace.start();
  assert.equal(getElement("page-title").textContent, startEmpty ? "Your flight deck" : "Flight");
  return {
    workspace, element: getElement, poll: () => poll(), summaryStatuses,
    update: () => { remote = profile(2); },
    create: () => { remote = profile(1); },
    failNextProfile: () => { failedProfile = true; },
    profileReads: () => profileReads,
    blockNextProfile: () => {
      let started, release;
      const waiting = new Promise(resolve => { started = resolve; });
      const wait = new Promise(resolve => { release = resolve; });
      blockedProfile = { started, wait };
      return { waiting, release };
    },
    blockNextProfileBody: () => {
      let started, release;
      const waiting = new Promise(resolve => { started = resolve; });
      const wait = new Promise(resolve => { release = resolve; });
      blockedBody = { started, wait };
      return { waiting, release };
    }
  };
}

test("the only saved profile is selected automatically when there is no saved preference", async () => {
  const view = await fixture({ preference: null });
  assert.equal(view.element("profile-select").value, "ship");
  assert.equal(view.element("page-title").textContent, "Flight");
});

for (const preference of [null, { profile: "ship", page: "flight" }]) {
  test(`automatic selection preserves a new draft during polling (preference: ${!!preference})`, async () => {
    const view = await fixture({ preference, startEmpty: true });
    view.create();
    const blocked = view.blockNextProfile();
    const polling = view.poll();
    await blocked.waiting;
    await view.element("new-profile").click();
    view.element("profile-name").value = "My new draft";
    view.element("profile-name").listeners.get("input")({ target: view.element("profile-name") });
    blocked.release();
    await polling;
    assert.equal(view.element("profile-select").value, "draft");
    assert.equal(view.element("profile-name").value, "My new draft");
    assert.equal(view.element("page-title").textContent, "Page 1");
    assert.equal(view.element("editor-toolbar").hidden, false);
    await view.poll();
    assert.equal(view.element("profile-select").value, "draft");
    assert.equal(view.element("profile-name").value, "My new draft");
  });
}

test("a profile response completing after reset does not restore a previous session", async () => {
  const view = await fixture({ preference: null, startEmpty: true });
  view.create();
  const blocked = view.blockNextProfileBody();
  const polling = view.poll();
  await blocked.waiting;
  view.workspace.reset();
  blocked.release();
  await polling;
  assert.equal(view.element("profile-select").value, "");
  assert.equal(view.element("page-title").textContent, "Your flight deck");
});

test("a failed active-profile fetch is retried on 304 and updates the layout and trigger mappings", async () => {
  const view = await fixture();
  view.workspace.receive({ stream_id: "game", connection_status: "connected", events: [] });
  view.update();
  view.failNextProfile();
  await view.poll();
  assert.match(view.element("workspace-message").textContent, /Temporary profile read failure/);
  assert.equal(view.element("page-title").textContent, "Flight");
  await view.poll();
  assert.deepEqual(view.summaryStatuses, [200, 200, 304]);
  assert.equal(view.profileReads(), 3);
  assert.equal(view.element("page-title").textContent, "Updated flight");
  view.workspace.receive({ stream_id: "game", connection_status: "connected", events: [{ sequence: 1, type: enter }] });
  assert.equal(view.element("page-title").textContent, "Updated crew");
  await view.poll();
  assert.equal(view.profileReads(), 3);
});

test("a profile fetch superseded by page navigation is retried without losing the manual page", async () => {
  const view = await fixture();
  view.update();
  const blocked = view.blockNextProfile();
  const polling = view.poll();
  await blocked.waiting;
  view.element("page-navigation").children[1].click();
  blocked.release();
  await polling;
  assert.equal(view.element("page-title").textContent, "Crew");
  await view.poll();
  assert.deepEqual(view.summaryStatuses, [200, 200, 304]);
  assert.equal(view.profileReads(), 3);
  assert.equal(view.element("page-title").textContent, "Updated crew");
});

test("cached summaries continue warning about a remote revision without overwriting an editing draft", async () => {
  const view = await fixture();
  await view.element("edit-profile").click();
  view.update();
  await view.poll();
  view.element("workspace-message").textContent = "";
  await view.poll();
  assert.deepEqual(view.summaryStatuses, [200, 200, 304]);
  assert.equal(view.profileReads(), 2);
  assert.equal(view.element("page-title").textContent, "Flight");
  assert.equal(view.element("editor-toolbar").hidden, false);
  assert.equal(view.element("conflict-actions").hidden, false);
  assert.match(view.element("workspace-message").textContent, /draft has not been overwritten/);
});
