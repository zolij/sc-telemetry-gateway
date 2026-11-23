const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");

const assets = path.join(__dirname, "../../src/VerseDataGateway.Windows/mfd/assets");
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

async function fixture({ preference = { profile: "ship", page: "flight" }, startEmpty = false, withAction = false, mappedAction = false, group = null, groupField = "" } = {}) {
  const elements = new Map();
  function element() {
    const queries = new Map();
    return {
      children: [], dataset: {}, value: "", hidden: false, disabled: false, textContent: "", listeners: new Map(),
      classList: { toggle() {} },
      append(...items) { this.children.push(...items); },
      replaceChildren(...items) { this.children = items; },
      get childElementCount() { return this.children.length; },
      get options() { return this.children; },
      get selectedOptions() { return this.children.filter(option => option.value === this.value); },
      setAttribute() {},
      focus() {},
      select() {},
      close() {},
      showModal() {},
      addEventListener(type, callback) { this.listeners.set(type, callback); },
      click() { return this.listeners.get("click")?.(); },
      querySelector(selector) {
        if (!queries.has(selector)) queries.set(selector, element());
        return queries.get(selector);
      }
    };
  }
  const getElement = id => {
    if (!elements.has(id)) {
      const item = element();
      const choices = {
        "tile-widget": ["value", "status", "list", "action"],
        "tile-format": ["auto", "number", "percent_fraction", "percent", "percent_max"]
      }[id];
      if (choices) item.append(...choices.map(value => Object.assign(element(), { value })));
      elements.set(id, item);
    }
    return elements.get(id);
  };
  let remote = startEmpty ? null : profile(1), poll, failedProfile = false, blockedProfile = null, blockedBody = null, profileReads = 0;
  const actionRequests = [];
  const shieldPower = "vehicle.state.power.shield_generators";
  const shieldLevel = "vehicle.state.shields.level";
  const fields = mappedAction ? [
    { path: shieldPower, repeated: false, numeric: false },
    { path: shieldLevel, repeated: false, numeric: true },
    { path: "vehicle.state.lights_enabled", repeated: false, numeric: false }
  ] : groupField ? [{ path: groupField, repeated: false, numeric: false }] : [];
  let actionCatalog = group ? group.map(control => ({
    action_name: control.action, category: "group", holdable: control.hold_ms != null,
    field: groupField || null, fields: groupField ? [groupField] : [], enabled: true
  })) : mappedAction ? [{
    action_name: "v_power_toggle_shields", category: "spaceship_power", holdable: false,
    field: shieldPower, fields: [shieldPower, shieldLevel], enabled: true
  }] : withAction ? [{
    action_name: "v_invoke_ping", category: "spaceship_radar", holdable: true, field: null, enabled: true
  }] : [];
  if (withAction || mappedAction || group) {
    remote.pages[0].id = "bb2351a5-6dca-41be-a2e4-e7a33bb93503";
    remote.pages[1].id = "f1fc4de5-0b85-459b-9a7c-020da373a053";
    remote.pages[0].tiles.push({
      id: "cf08801a-1e07-4d0d-b84a-b5cf0e616323",
      field: group ? groupField : mappedAction ? shieldLevel : "", label: group ? "Group" : mappedAction ? "Shields" : "Radar",
      widget: (group ? !!groupField : mappedAction) ? "value" : "action", size: "normal", decimals: 2, format: "auto", rules: [],
      ...(group ? { controls: group } : {
        action: mappedAction ? "v_power_toggle_shields" : "v_invoke_ping",
        ...(!mappedAction ? { hold_ms: 500 } : {})
      })
    });
  }
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
      if (url.endsWith("/fields")) return response(200, fields);
      if (url.endsWith("/events")) return response(200, [enter]);
      if (url.endsWith("/actions")) return response(200, actionCatalog);
      if (url.endsWith("/actions/trigger")) {
        actionRequests.push(JSON.parse(options.body));
        return response(200, { status: "Ok", message: "" });
      }
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
    workspace, element: getElement, poll: () => poll(), summaryStatuses, actionRequests,
    setActions: value => { actionCatalog = value; },
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

test("action tile sends a bounded command only when online and reports acceptance", async () => {
  const view = await fixture({ withAction: true });
  const card = view.element("custom-cards").children[0];
  const button = card.children[3], feedback = card.children[5];
  const snapshot = { selected_fields: [], fields: {}, connection_status: "connected" };
  view.workspace.paint(snapshot, false, 0, () => "");
  assert.equal(button.disabled, true);
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(button.disabled, false);
  await button.click();
  assert.deepEqual(view.actionRequests, [{ action_name: "v_invoke_ping", hold_ms: 500 }]);
  assert.match(feedback.textContent, /ACCEPTED.*CHECK GAME STATE/);
  view.workspace.paint(snapshot, false, 0, () => "");
  assert.equal(button.disabled, true);
});

test("small combined groups dispatch the selected button and require the displayed field", async () => {
  const group = [{ action: "v_master_mode_set_nav" }, { action: "v_master_mode_set_scm" }];
  const field = "vehicle.state.master_mode.mode";
  const view = await fixture({ group, groupField: field });
  const buttons = view.element("custom-cards").children[0].children[3].children;
  const snapshot = { selected_fields: [], fields: {}, connection_status: "connected" };
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.ok(buttons.every(button => button.disabled));
  snapshot.selected_fields.push(field);
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.ok(buttons.every(button => !button.disabled));
  await buttons[1].click();
  assert.deepEqual(view.actionRequests, [{ action_name: "v_master_mode_set_scm" }]);
  assert.match(view.element("custom-cards").children[0].children[5].textContent, /Master Mode Set Scm.*ACCEPTED/);
  view.setActions([{ action_name: group[0].action, category: "group", holdable: false, field, fields: [field], enabled: true }]);
  await view.poll();
  const updated = view.element("custom-cards").children[0].children[3].children;
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(updated[0].disabled, false);
  assert.equal(updated[1].disabled, true);
});

test("large groups require a selection and retain each control's hold duration", async () => {
  const group = Array.from({ length: 5 }, (_, index) => ({ action: "emote_" + index }));
  group[3].hold_ms = 500;
  const view = await fixture({ group });
  const [select, button] = view.element("custom-cards").children[0].children[3].children;
  const snapshot = { selected_fields: [], fields: {}, connection_status: "connected" };
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(button.disabled, true);
  select.value = group[3].action;
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(button.disabled, false);
  await button.click();
  assert.deepEqual(view.actionRequests, [{ action_name: "emote_3", hold_ms: 500 }]);
  view.workspace.paint(snapshot, false, 0, () => "");
  assert.equal(button.disabled, true);
});

test("editing grouped controls preserves the group and rejects duplicates", async () => {
  const group = [{ action: "emote_wave" }, { action: "emote_salute" }];
  const view = await fixture({ group });
  await view.element("edit-profile").click();
  await view.element("custom-cards").children[0].children[3].children[1].click();
  assert.equal(view.element("tile-controls-list").children.length, 2);
  view.element("tile-action").value = "emote_wave";
  view.element("add-tile-control").click();
  assert.match(view.element("tile-message").textContent, /already/);
  view.element("tile-action").value = "";
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.equal(view.element("tile-message").textContent, "");
  assert.equal(view.element("custom-cards").children.length, 1);
  assert.deepEqual(view.actionRequests, []);
});

test("editing converts a legacy control into a group without losing its hold", async () => {
  const view = await fixture({ withAction: true });
  view.setActions([
    { action_name: "v_invoke_ping", category: "radar", holdable: true, field: null, enabled: true },
    { action_name: "emote_wave", category: "emote", holdable: false, field: null, enabled: true }
  ]);
  await view.poll();
  await view.element("edit-profile").click();
  await view.element("custom-cards").children[0].children[3].children[1].click();
  view.element("add-tile-control").click();
  assert.match(view.element("tile-controls-list").children[0].children[0].textContent, /500 MS/);
  view.element("tile-action").value = "emote_wave";
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.equal(view.element("tile-message").textContent, "");
  await view.element("custom-cards").children[0].children[3].children[1].click();
  assert.equal(view.element("tile-controls-list").children.length, 2);
  assert.match(view.element("tile-controls-list").children[0].children[0].textContent, /500 MS/);
  assert.deepEqual(view.actionRequests, []);
});

test("combined controls require their own selected and still-associated field", async () => {
  const view = await fixture({ mappedAction: true });
  let button = view.element("custom-cards").children[0].children[3];
  const power = "vehicle.state.power.shield_generators", level = "vehicle.state.shields.level";
  const snapshot = { selected_fields: [power], fields: {}, connection_status: "connected" };
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(button.disabled, true);
  snapshot.selected_fields.push(level);
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(button.disabled, false);
  view.setActions([{ action_name: "v_power_toggle_shields", category: "spaceship_power", holdable: false,
    field: power, fields: [power], enabled: true }]);
  await view.poll();
  button = view.element("custom-cards").children[0].children[3];
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(button.disabled, true);
  assert.match(button.title, /no longer linked/);
});

test("editor links secondary fields and creates combined controls from the library", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  await view.element("custom-cards").children[0].children[3].children[1].click();
  assert.equal(view.element("tile-field").value, "vehicle.state.shields.level");
  assert.equal(view.element("tile-action").value, "v_power_toggle_shields");
  assert.ok(view.element("tile-action").options.some(option => option.value === "v_power_toggle_shields"));
  view.element("tile-field").value = "vehicle.state.power.shield_generators";
  view.element("tile-field").listeners.get("change")();
  assert.equal(view.element("tile-action").value, "v_power_toggle_shields");
  view.element("tile-field").value = "vehicle.state.lights_enabled";
  view.element("tile-field").listeners.get("change")();
  assert.match(view.element("tile-action").options.at(-1).textContent, /not linked/);
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.match(view.element("tile-message").textContent, /not linked/);
  view.element("action-results").children[1].click();
  assert.equal(view.element("tile-widget").value, "value");
  assert.equal(view.element("tile-field").value, "vehicle.state.power.shield_generators");
  assert.equal(view.element("tile-action").value, "v_power_toggle_shields");
  assert.deepEqual(view.actionRequests, []);
});

test("editor supports legacy single-field action catalogs and converting standalone controls", async () => {
  const view = await fixture({ mappedAction: true });
  view.setActions([{ action_name: "v_power_toggle_shields", category: "spaceship_power", holdable: false,
    field: "vehicle.state.power.shield_generators", enabled: true }]);
  await view.poll();
  await view.element("edit-profile").click();
  view.element("action-results").children[1].click();
  assert.equal(view.element("tile-action").value, "v_power_toggle_shields");
  view.element("tile-widget").value = "action";
  view.element("tile-widget").listeners.get("change")();
  assert.equal(view.element("tile-field").disabled, true);
  view.element("tile-widget").value = "value";
  view.element("tile-widget").listeners.get("change")();
  assert.equal(view.element("tile-field").disabled, false);
  assert.equal(view.element("tile-action").value, "v_power_toggle_shields");
});

test("editing can add a game control without triggering it", async () => {
  const view = await fixture({ withAction: true });
  await view.element("edit-profile").click();
  const action = view.element("action-results").children[1];
  assert.ok(action);
  action.click();
  assert.equal(view.element("tile-widget").value, "action");
  assert.equal(view.element("tile-action").value, "v_invoke_ping");
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.equal(view.element("tile-message").textContent, "");
  assert.equal(view.element("custom-cards").children.length, 2);
  assert.deepEqual(view.actionRequests, []);
});

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
