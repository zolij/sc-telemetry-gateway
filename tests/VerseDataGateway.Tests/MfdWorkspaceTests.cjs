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
test("offline catalog matches the documented PTU snapshot", () => {
  const docs = fs.readFileSync(path.join(__dirname, "../../docs/GAME-TELEMETRY.md"), "utf8");
  const source = fs.readFileSync(path.join(__dirname, "../../src/VerseDataGateway.Core/MfdActionLibrary.cs"), "utf8");
  const documented = docs.split("## Observed action catalogue (not part of the proto)")[1]
    .split("```text")[1].split("```")[0];
  const baseline = source.split('private const string Baseline = """')[1].split('"""')[0];
  const actual = new Map();
  for (const line of baseline.trim().split("\n")) {
    const [group, names] = line.trim().split(": ");
    for (const name of names.split(" ")) actual.set(name, group);
  }
  assert.equal(actual.size, 112);
  for (const input of [documented.replace(/\r?\n/g, "\n"), documented.replace(/\r?\n/g, "\r\n")]) {
    const expected = new Map();
    let category;
    for (const line of input.split(/\r?\n/)) {
      const header = /^(\w+) \(\d+\)$/.exec(line);
      if (header) category = header[1];
      else if (/^  \w+$/.test(line)) expected.set(line.trim(), category);
    }
    assert.deepEqual([...actual].sort(), [...expected].sort());
  }
});
const profile = revision => ({
  schema_version: 1, id: "ship", name: "Ship", revision,
  pages: [
    { id: "flight", name: revision === 1 ? "Flight" : "Updated flight", tiles: [], trigger_events: [] },
    { id: "crew", name: revision === 1 ? "Crew" : "Updated crew", tiles: [], trigger_events: revision === 1 ? [] : [enter] }
  ]
});

async function fixture({ preference = { profile: "ship", page: "flight" }, startEmpty = false, withAction = false, mappedAction = false, gameUnavailable = false, libraryUnavailable = false, combined = false, twoActions = false, actionsOnly = false, previousLights = false } = {}) {
  const elements = new Map();
  function element() {
    const queries = new Map();
    return {
      children: [], dataset: {}, value: "", hidden: false, disabled: false, textContent: "", listeners: new Map(),
      classList: { toggle() {}, add() {}, remove() {} },
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
  let remote = startEmpty ? null : profile(1), poll, failedProfile = false, blockedProfile = null, blockedBody = null, blockedAction = null, blockedSave = null, profileReads = 0;
  const actionRequests = [];
  const savedBodies = [];
  const shieldPower = "vehicle.state.power.shield_generators";
  const shieldLevel = "vehicle.state.shields.level";
  const fields = mappedAction ? [
    { path: shieldPower, repeated: false, numeric: false },
    { path: shieldLevel, repeated: false, numeric: true },
    { path: "vehicle.state.lights_enabled", repeated: false, numeric: false },
    ...["vehicle.state.flight.state.gear", "vehicle.state.master_mode.mode",
      "vehicle.state.flight.settings.advanced_hud_enabled_scm", "vehicle.state.flight.settings.advanced_hud_enabled_nav",
      "vehicle.state.power.weapons", "vehicle.state.power.shield_emitters", "vehicle.state.power.engines"]
      .map(path => ({ path, repeated: false, numeric: false, type: path.endsWith(".gear") || path.endsWith(".mode") ? "enum" : "bool" }))
  ] : [];
  let actionCatalog = mappedAction ? [{
    action_name: "v_power_toggle_shields", category: "spaceship_power", holdable: false,
    fields: [shieldPower, shieldLevel], enabled: true
  }] : withAction ? [{
    action_name: "v_invoke_ping", category: "spaceship_radar", holdable: true, fields: [], enabled: true
  }] : [];
  if (twoActions) actionCatalog.push({
    action_name: "v_other", category: "spaceship_power", holdable: false, fields: [shieldPower], enabled: false
  });
  if (withAction || mappedAction) {
    remote.pages[0].id = "bb2351a5-6dca-41be-a2e4-e7a33bb93503";
    remote.pages[1].id = "f1fc4de5-0b85-459b-9a7c-020da373a053";
    remote.pages[0].tiles.push({
      id: "cf08801a-1e07-4d0d-b84a-b5cf0e616323",
      field: "", label: mappedAction ? "Shields" : "Radar",
      widget: "action", size: "normal", decimals: 2, format: "auto", rules: [],
      action: mappedAction ? "v_power_toggle_shields" : "v_invoke_ping",
      ...(!mappedAction ? { hold_ms: 500 } : {})
    });
    if (combined) {
      const control = remote.pages[0].tiles[0];
      remote.pages[0].tiles = [{
        id: "14a8e3ae-a346-4d86-bdc0-58d0db8984e4", field: "", label: "Shields",
        widget: "combined", size: "wide", decimals: 2, format: "auto", rules: [],
        components: [
          { id: "8bf4dcd1-f27e-48dd-8804-b707b01ec8ba", field: shieldPower, label: "Power",
            widget: "value", size: "normal", decimals: 2, format: "auto", rules: [] },
          control
        ]
      }];
      if (twoActions) remote.pages[0].tiles[0].components.push({
        ...control, id: "092eb896-64a8-457b-8ec6-05c8765c368f", label: "Other", action: "v_other"
      });
      if (actionsOnly) remote.pages[0].tiles[0].components.shift();
    }
    if (previousLights) {
      remote.pages[0].id = "bb2351a5-6dca-41be-a2e4-e7a33bb93503";
      remote.pages[1].id = "f1fc4de5-0b85-459b-9a7c-020da373a053";
      const base = { size: "normal", decimals: 2, format: "auto", rules: [] };
      remote.pages[0].tiles = [{
        ...base, id: "14a8e3ae-a346-4d86-bdc0-58d0db8984e4", field: "", label: "Vehicle lights",
        widget: "combined", size: "wide", components: [
          { ...base, id: "8bf4dcd1-f27e-48dd-8804-b707b01ec8ba",
            field: "vehicle.state.lights_enabled", label: "Vehicle lights", widget: "value" },
          ...["v_lights", "v_lights_on", "v_lights_off"].map((action, index) => ({
            ...base, id: `00000000-0000-4000-8000-00000000000${index + 1}`,
            field: "", label: ["Lights", "Lights On", "Lights Off"][index], widget: "action", action
          }))
        ]
      }];
    }
  }
  const summaryStatuses = [];
  const response = (status, data, etag = null) => ({
    status, ok: status >= 200 && status < 300,
    headers: { get: () => etag },
    json: async () => structuredClone(data)
  });
  const workspace = vm.runInNewContext(source + "\nMfdWorkspace;", {
    document: { getElementById: getElement, createElement: element, querySelector: getElement, querySelectorAll: selector => {
      if (!selector.includes(".combined-settings input")) return [];
      const groups = getElement("custom-cards").children;
      return groups.flatMap(group => group.children.filter(child => child.className === "combined-settings").flatMap(child => child.children));
    } },
    window: { addEventListener() {}, confirm: () => true },
    localStorage: { getItem: () => preference ? JSON.stringify(preference) : null, setItem() {} },
    MfdFocus: { update() {} }, AbortSignal, TextEncoder, crypto: webcrypto,
    Option: function (text, value) { return Object.assign(element(), { textContent: text, value }); },
    setInterval: callback => { poll = callback; },
    fetch: async (url, options) => {
      if (url.endsWith("/fields")) return response(200, fields);
      if (url.endsWith("/events")) return response(200, [enter]);
      if (url.endsWith("/actions")) return gameUnavailable
        ? response(503, { message: "Game unavailable" }) : response(200, actionCatalog);
      if (url.endsWith("/action-library")) return libraryUnavailable
        ? response(503, { message: "Action library unreadable" }) : response(200, actionCatalog);
      if (url.endsWith("/actions/trigger")) {
        actionRequests.push(JSON.parse(options.body));
        if (blockedAction) {
          const blocked = blockedAction;
          blockedAction = null;
          blocked.started();
          await blocked.wait;
          if (blocked.fail) throw new Error("Connection lost");
        }
        return response(200, { status: "Ok", message: "" });
      }
      if (url.endsWith("/profiles")) {
        const etag = '"' + (remote?.revision ?? "empty") + '"';
        const status = options.headers["If-None-Match"] === etag ? 304 : 200;
        summaryStatuses.push(status);
        return response(status, remote ? [{ id: remote.id, name: remote.name, revision: remote.revision }] : [], etag);
      }
      assert.equal(url, "/api/mfd/profiles/ship");
      if (options.method === "PUT") {
        const body = JSON.parse(options.body);
        savedBodies.push(body);
        if (blockedSave) { const blocked = blockedSave; blockedSave = null; blocked.started(); await blocked.wait; }
        remote = { ...body, revision: body.revision + 1 };
        return response(200, remote);
      }
      if (options.method === "DELETE") {
        assert.equal(options.headers["If-Match"], '"' + remote.revision + '"');
        remote = null;
        return response(204, null);
      }
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
  workspace.init(() => "test-token", () =>
    workspace.paint({ selected_fields: [], fields: {}, connection_status: "connected" }, true, 0, () => ""));
  await workspace.start();
  assert.equal(getElement("page-title").textContent, startEmpty ? "Your flight deck" : "Flight");
  return {
    workspace, element: getElement, poll: () => poll(), summaryStatuses, actionRequests, savedBodies,
    setActions: value => { actionCatalog = value; },
    changeActionCatalog: () => { actionCatalog = actionCatalog.map(action => ({ ...action, mapping_verified: true })); },
    update: () => { remote = profile(2); },
    create: () => { remote = profile(1); },
    failNextProfile: () => { failedProfile = true; },
    profileReads: () => profileReads,
    blockNextSave: () => {
      let started, release;
      const waiting = new Promise(resolve => { started = resolve; });
      const wait = new Promise(resolve => { release = resolve; });
      blockedSave = { started, wait };
      return { waiting, release };
    },
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
    },
    blockNextAction: fail => {
      let started, release;
      const waiting = new Promise(resolve => { started = resolve; });
      const wait = new Promise(resolve => { release = resolve; });
      blockedAction = { started, wait, fail };
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

test("available controls are re-enabled after a profile operation without another telemetry frame", async () => {
  const view = await fixture({ withAction: true });
  const button = view.element("custom-cards").children[0].children[3];
  assert.equal(button.disabled, false);
  assert.equal(button.title, "");
});

test("live controls and editing remain available if the saved action library fails", async () => {
  const view = await fixture({ withAction: true, libraryUnavailable: true });
  assert.equal(view.element("custom-cards").children[0].children[3].disabled, false);
  await view.element("edit-profile").click();
  assert.match(view.element("action-results").children[1].textContent, /unreadable/);
  assert.equal(view.element("action-results").children[2].children[0].textContent, "Invoke Ping");
});

test("combined settings are disabled while a save is in flight", async () => {
  const view = await fixture({ mappedAction: true, combined: true });
  await view.element("edit-profile").click();
  const settings = view.element("custom-cards").children[0].children[1].children;
  const blocked = view.blockNextSave();
  const pending = view.element("save-profile").click();
  await blocked.waiting;
  assert.ok(settings.every(item => item.disabled));
  settings[0].value = "Changed while saving";
  settings[0].listeners.get("input")();
  blocked.release();
  await pending;
  assert.equal(view.savedBodies[0].pages[0].tiles[0].label, "Shields");
  assert.equal(view.element("custom-cards").children[0].children[0].textContent, "Shields");
});

for (const fail of [false, true]) {
  test(`action feedback survives navigation and polling rebuilds (${fail ? "unknown outcome" : "accepted"})`, async () => {
    const view = await fixture({ withAction: true });
    view.workspace.paint({ selected_fields: [], fields: {}, connection_status: "connected" }, true, 0, () => "");
    const blocked = view.blockNextAction(fail);
    const pending = view.element("custom-cards").children[0].children[3].click();
    await blocked.waiting;
    assert.equal(view.element("custom-cards").children[0].children[5].textContent, "SENDING");
    view.element("page-navigation").children[1].click();
    view.element("page-navigation").children[0].click();
    let card = view.element("custom-cards").children[0];
    assert.equal(card.children[5].textContent, "SENDING");
    assert.equal(card.children[3].disabled, true);
    view.changeActionCatalog();
    await view.poll();
    card = view.element("custom-cards").children[0];
    assert.equal(card.children[5].textContent, "SENDING");
    assert.equal(card.children[3].disabled, true);
    blocked.release();
    await pending;
    assert.match(card.children[5].textContent, fail ? /may have executed.*check the game/ : /ACCEPTED.*CHECK GAME STATE/);
    view.element("page-navigation").children[1].click();
    view.element("page-navigation").children[0].click();
    card = view.element("custom-cards").children[0];
    assert.match(card.children[5].textContent, fail ? /may have executed.*check the game/ : /ACCEPTED.*CHECK GAME STATE/);
    assert.equal(card.children[3].disabled, false);
    assert.equal(view.actionRequests.length, 1);
  });
}

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

test("known controls remain editable when the game is unavailable", async () => {
  const view = await fixture({ withAction: true, gameUnavailable: true });
  await view.element("edit-profile").click();
  assert.match(view.element("action-results").children[1].textContent, /Game unavailable/);
  view.element("action-results").children[2].click();
  assert.equal(view.element("tile-action").value, "v_invoke_ping");
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.equal(view.element("tile-message").textContent, "");
  assert.deepEqual(view.actionRequests, []);
});

test("the editor keeps mapped controls and telemetry on separate tiles", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  view.element("action-results").children[1].click();
  assert.equal(view.element("tile-widget").value, "action");
  assert.equal(view.element("tile-field").disabled, true);
  assert.ok(view.element("tile-widget").options.find(option => option.value === "value").disabled);
  view.element("field-results").children[0].children[1].click();
  assert.equal(view.element("tile-widget").value, "value");
  assert.equal(view.element("tile-action").disabled, true);
  assert.ok(view.element("tile-widget").options.find(option => option.value === "action").disabled);
  assert.deepEqual(view.actionRequests, []);
});

test("the editor combines existing tiles and adds a field without triggering controls", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  const actionCard = view.element("custom-cards").children[0];
  const combine = actionCard.children.at(-1).children.find(button => button.textContent === "COMBINE");
  await combine.click();
  let group = view.element("custom-cards").children[0];
  assert.equal(group.dataset.widget, "combined");
  assert.equal(group.children[2].children.length, 1);
  view.element("field-results").children[0].children[1].click();
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.equal(view.element("tile-message").textContent, "");
  group = view.element("custom-cards").children[0];
  assert.equal(group.children[2].children.length, 2);
  await view.element("save-profile").click();
  assert.equal(view.savedBodies[0].pages[0].tiles[0].components.length, 2);
  assert.deepEqual(view.actionRequests, []);
});

test("the lights template includes the observed field and controls even if unselected", async () => {
  const view = await fixture({ withAction: true, gameUnavailable: true });
  await view.element("edit-profile").click();
  const template = view.element("preset-results").children[1];
  assert.equal(template.children[0].textContent, "Vehicle lights");
  template.click();
  const preview = view.element("custom-cards").children[1].children[2].children;
  assert.deepEqual(preview.slice(1).map(item => item.children[0].textContent), ["Toggle"]);
  await view.element("save-profile").click();
  const group = view.savedBodies[0].pages[0].tiles[1];
  assert.equal(group.widget, "combined");
  assert.equal(group.components[0].field, "vehicle.state.lights_enabled");
  assert.deepEqual(group.components.slice(1).map(item => item.action), ["v_lights"]);
  assert.deepEqual(view.actionRequests, []);
});

test("all built-in templates render direct, descriptive controls", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  const expected = [
    ["Toggle"], ["Toggle"], ["Toggle"],
    ["Toggle"], ["Cycle"],
    ["Toggle"], ["Toggle"], ["Toggle", "Toggle", "Toggle"]
  ];
  for (let index = 0; index < expected.length; index++) {
    view.element("preset-results").children[index + 1].click();
    const group = view.element("custom-cards").children.at(-1);
    const components = group.children[2].children;
    assert.deepEqual(components.filter(item => item.dataset.widget === "action").map(item => item.children[0].textContent), expected[index]);
    assert.ok(components.every(item => !item.className?.includes("instrument size-")));
  }
  await view.element("save-profile").click();
  const actionSets = view.savedBodies[0].pages[0].tiles.slice(1).map(group =>
    group.components.filter(item => item.widget === "action").map(item => item.action));
  assert.deepEqual(actionSets, [
    ["v_lights"], ["v_toggle_landing_system"], ["v_ifcs_toggle_gforce_safety"],
    ["v_ifcs_vector_decoupling_toggle"], ["v_master_mode_cycle"],
    ["v_flight_advanced_hud_toggle"], ["v_light_amplification_toggle"],
    ["v_power_toggle_weapons", "v_power_toggle_shields", "v_power_toggle_thrusters"]
  ]);
  assert.deepEqual(view.actionRequests, []);
});

test("previously saved template controls remain visible and survive editing", async () => {
  const view = await fixture({ mappedAction: true, previousLights: true });
  const components = view.element("custom-cards").children[0].children[1].children;
  assert.deepEqual(components.slice(1).map(item => item.children[0].textContent), ["Toggle", "On", "Off"]);
  await view.element("edit-profile").click();
  await view.element("save-profile").click();
  assert.deepEqual(view.savedBodies[0].pages[0].tiles[0].components.slice(1).map(item => item.action),
    ["v_lights", "v_lights_on", "v_lights_off"]);
  assert.deepEqual(view.actionRequests, []);
});

test("landing gear toggle disables during deployment and retraction", async () => {
  const view = await fixture({ mappedAction: true });
  view.setActions([{ action_name: "v_toggle_landing_system", category: "spaceship_movement", enabled: true, holdable: false }]);
  await view.poll();
  await view.element("edit-profile").click();
  view.element("preset-results").children[2].click();
  await view.element("save-profile").click();
  const group = view.element("custom-cards").children[1];
  const button = group.children[1].children[1].children[0];
  const path = "vehicle.state.flight.state.gear";
  const snapshot = { selected_fields: [path], fields: { [path]: { value: "", age_ms: 0, last_known: false } } };
  for (const [state, disabled] of [["RETRACTED", false], ["DEPLOYING", true], ["DEPLOYED", false], ["RETRACTING", true]]) {
    snapshot.fields[path].value = "VEHICLE_COMPONENT_DEPLOYMENT_STATE_" + state;
    view.workspace.paint(snapshot, true, 0, () => "");
    assert.equal(button.disabled, disabled, state);
    if (disabled) assert.match(group.children[2].textContent, /TRANSITION/);
  }
});

test("advanced HUD displays only the current fresh SCM or NAV state", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  view.element("preset-results").children[6].click();
  await view.element("save-profile").click();
  const group = view.element("custom-cards").children[1];
  const [mode, scm, nav] = group.children[1].children;
  const master = "vehicle.state.master_mode.mode";
  const scmPath = "vehicle.state.flight.settings.advanced_hud_enabled_scm";
  const navPath = "vehicle.state.flight.settings.advanced_hud_enabled_nav";
  const field = value => ({ value, age_ms: 0, last_known: false });
  const snapshot = { selected_fields: [master, scmPath, navPath], fields: {
    [master]: field("VEHICLE_MASTER_MODE_SCM"), [scmPath]: field(true), [navPath]: field(false)
  } };
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(mode.hidden, true); assert.equal(scm.hidden, false); assert.equal(nav.hidden, true);
  assert.equal(scm.children[1].textContent, "On");
  snapshot.fields[master].value = "VEHICLE_MASTER_MODE_NAVIGATION";
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(scm.hidden, true); assert.equal(nav.hidden, false);
  assert.equal(nav.children[1].textContent, "Off");
  snapshot.fields[master].age_ms = 2000;
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(mode.hidden, false); assert.equal(scm.hidden, true); assert.equal(nav.hidden, true);
  assert.equal(mode.children[1].textContent, "Mode unavailable");
  snapshot.fields[master].age_ms = 0;
  snapshot.fields[master].value = "VEHICLE_MASTER_MODE_STEALTH";
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(mode.children[1].textContent, "Mode unavailable");
});

test("system power combines shield states and provides three independent toggles", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  view.element("preset-results").children[8].click();
  await view.element("save-profile").click();
  const group = view.element("custom-cards").children[1];
  const [weapons, shields, engines] = group.children[1].children;
  assert.deepEqual([weapons, shields, engines].map(row => row.children[0].textContent), ["Weapons", "Shields", "Engines"]);
  const paths = ["vehicle.state.power.weapons", "vehicle.state.power.shield_generators",
    "vehicle.state.power.shield_emitters", "vehicle.state.power.engines"];
  const snapshot = { selected_fields: paths, fields: Object.fromEntries(paths.map((path, index) =>
    [path, { value: index !== 2, age_ms: 0, last_known: false }])) };
  const [shield, emitter] = shields.children[1].children;
  assert.equal(emitter.hidden, true);
  for (const [generator, emitterValue, expected] of [[true, true, "On"], [false, false, "Off"], [true, false, "Mixed"], [false, true, "Mixed"]]) {
    snapshot.fields[paths[1]].value = generator;
    snapshot.fields[paths[2]].value = emitterValue;
    view.workspace.paint(snapshot, true, 0, age => `${age} ms`);
    assert.equal(shield.children[1].textContent, expected);
    assert.equal(shield.dataset.state, "live");
  }
  snapshot.fields[paths[1]].age_ms = 1900;
  snapshot.fields[paths[2]].age_ms = 100;
  view.workspace.paint(snapshot, true, 100, age => `${age} ms`);
  assert.equal(shield.dataset.state, "stale");
  assert.equal(shield.children[2].textContent, "STALE / 2000 ms");
  snapshot.fields[paths[1]].age_ms = 0;
  snapshot.fields[paths[2]].last_known = true;
  view.workspace.paint(snapshot, true, 0, age => `${age} ms`);
  assert.equal(shield.dataset.state, "last-known");
  snapshot.fields[paths[2]].last_known = false;
  view.workspace.paint(snapshot, false, 0, age => `${age} ms`);
  assert.equal(shield.dataset.state, "offline");
  snapshot.fields[paths[2]].value = null;
  view.workspace.paint(snapshot, true, 0, age => `${age} ms`);
  assert.equal(shield.children[1].textContent, "Unknown");
  assert.equal(shield.dataset.state, "unavailable");
  delete snapshot.fields[paths[2]];
  view.workspace.paint(snapshot, true, 0, age => `${age} ms`);
  assert.equal(shield.children[1].textContent, "Unknown");
  assert.equal(shield.children[2].textContent, "No value received");
  snapshot.selected_fields = paths.filter(path => path !== paths[2]);
  view.workspace.paint(snapshot, true, 0, age => `${age} ms`);
  assert.equal(shield.children[1].textContent, "Not selected");
  assert.match(shield.children[2].textContent, /both shield power fields/);
  assert.equal(shields.children[2].children[0].disabled, false);
  assert.equal(weapons.children[2].children[0].disabled, true);
  assert.equal(engines.children[2].children[0].disabled, true);
  assert.equal(group.children[2].textContent, "");
});

test("system power preserves explicit shield formats", async () => {
  for (const index of [1, 2]) {
    const view = await fixture({ mappedAction: true });
    await view.element("edit-profile").click();
    view.element("preset-results").children[8].click();
    const editor = view.element("custom-cards").children[1];
    const setup = editor.children[2].children[index].children.at(-1).children.find(button => button.textContent === "SETUP");
    await setup.click();
    view.element("tile-format").value = "number";
    view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
    await view.element("save-profile").click();
    const components = view.element("custom-cards").children[1].children[1].children;
    assert.equal(components.length, 7);
    const fields = ["vehicle.state.power.shield_generators", "vehicle.state.power.shield_emitters"];
    view.workspace.paint({ selected_fields: fields, fields: Object.fromEntries(fields.map(field =>
      [field, { value: true, age_ms: 0, last_known: false }])) }, true, 0, () => "");
    assert.equal(components[index].children[1].textContent, "Yes");
    assert.equal(components[2].hidden, false);
  }
});

test("built-in boolean states remain distinct from unavailable values", async () => {
  const view = await fixture({ mappedAction: true });
  await view.element("edit-profile").click();
  view.element("preset-results").children[1].click();
  await view.element("save-profile").click();
  const group = view.element("custom-cards").children[1];
  const value = group.children[1].children[0].children[0];
  const path = "vehicle.state.lights_enabled";
  const snapshot = { selected_fields: [path], fields: { [path]: { value: true, age_ms: 0, last_known: false } } };
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(value.textContent, "On");
  snapshot.fields[path].value = false;
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(value.textContent, "Off");
  snapshot.fields[path].value = null;
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(value.textContent, "Unknown");
  await view.element("edit-profile").click();
  const editorGroup = view.element("custom-cards").children[1];
  const setup = editorGroup.children[2].children[0].children.at(-1).children.find(button => button.textContent === "SETUP");
  await setup.click();
  view.element("tile-format").value = "number";
  view.element("tile-form").listeners.get("submit")({ preventDefault() {} });
  assert.equal(view.element("tile-message").textContent, "");
  await view.element("save-profile").click();
  snapshot.fields[path].value = true;
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(view.element("custom-cards").children[1].children[1].children[0].children[0].textContent, "Yes");
});

test("combined controls and telemetry update independently", async () => {
  const view = await fixture({ mappedAction: true, combined: true });
  const group = view.element("custom-cards").children[0];
  const [value, action] = group.children[1].children;
  const snapshot = {
    selected_fields: ["vehicle.state.power.shield_generators"],
    fields: { "vehicle.state.power.shield_generators": { value: true, age_ms: 0, last_known: false } }
  };
  view.workspace.paint(snapshot, true, 0, () => "now");
  assert.equal(value.children[1].textContent, "Yes");
  assert.equal(action.children[0].disabled, false);
  await action.children[0].click();
  assert.equal(view.actionRequests.length, 1);
  assert.match(action.children[2].textContent, /ACCEPTED/);
  view.workspace.paint(snapshot, false, 0, () => "now");
  assert.equal(action.children[0].disabled, true);
  assert.match(group.children[2].textContent, /CONTROLS.*GAME DISCONNECTED/);
  assert.equal(value.dataset.state, "offline");
});

test("combined controls show shared and individual unavailability without losing action feedback", async () => {
  const view = await fixture({ mappedAction: true, combined: true, twoActions: true });
  const group = view.element("custom-cards").children[0];
  const [, first, second] = group.children[1].children;
  const snapshot = { selected_fields: [], fields: {} };
  view.workspace.paint(snapshot, false, 0, () => "");
  assert.equal(first.children[1].textContent, "");
  assert.equal(second.children[1].textContent, "");
  assert.match(group.children[2].textContent, /GAME DISCONNECTED/);
  view.workspace.paint(snapshot, true, 0, () => "");
  assert.equal(first.children[0].disabled, false);
  assert.equal(second.children[0].disabled, true);
  assert.match(second.children[1].textContent, /matching field/);
  assert.doesNotMatch(group.children[2].textContent, /GAME DISCONNECTED/);
  await first.children[0].click();
  assert.match(first.children[2].textContent, /ACCEPTED/);
});

test("action-only combined cards retain unknown-outcome feedback after a refresh", async () => {
  const view = await fixture({ mappedAction: true, combined: true, actionsOnly: true });
  let group = view.element("custom-cards").children[0];
  view.workspace.paint({ selected_fields: [], fields: {} }, false, 0, () => "");
  assert.match(group.children[2].textContent, /GAME DISCONNECTED/);
  view.workspace.paint({ selected_fields: [], fields: {} }, true, 0, () => "");
  const blocked = view.blockNextAction(true);
  const pending = group.children[1].children[0].children[0].click();
  await blocked.waiting;
  view.changeActionCatalog();
  await view.poll();
  group = view.element("custom-cards").children[0];
  assert.equal(group.children[1].children[0].children[2].textContent, "SENDING");
  blocked.release();
  await pending;
  assert.match(group.children[1].children[0].children[2].textContent, /may have executed.*check the game/);
});

test("import rejects invalid tile properties before sending them to the PC", async () => {
  const view = await fixture();
  for (const extra of [{ action: "v_lights" }, { unexpected: true }]) {
    const data = profile(1);
    data.pages[0].id = "bb2351a5-6dca-41be-a2e4-e7a33bb93503";
    data.pages[1].id = "f1fc4de5-0b85-459b-9a7c-020da373a053";
    data.pages[0].tiles = [{ id: "cf08801a-1e07-4d0d-b84a-b5cf0e616323",
      field: "vehicle.state.lights_enabled", label: "Lights", widget: "value",
      size: "normal", decimals: 2, format: "auto", rules: [], ...extra }];
    const file = { size: 100, text: async () => JSON.stringify(data) };
    view.element("import-file").listeners.get("change")({ target: { files: [file] } });
    await new Promise(resolve => setImmediate(resolve));
    assert.match(view.element("workspace-message").textContent, /Invalid tile settings/);
  }
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
