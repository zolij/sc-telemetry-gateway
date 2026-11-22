const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../../src/ScTelemetryGateway.Windows/mfd/assets/triggers.js"), "utf8");
const triggers = vm.runInNewContext(source + "\nMfdPageTriggers;");
const enter = "GAME_EVENT_TYPE_ACTOR_ENTER_SEAT";
const leave = "GAME_EVENT_TYPE_ACTOR_LEAVE_SEAT";
const pages = () => [{ id: "flight", name: "Flight", trigger_events: [enter] },
  { id: "crew", name: "Crew", trigger_events: [leave] }];
const snapshot = (events, stream = "game") => ({ stream_id: stream, events });
const event = (sequence, type = enter) => ({ sequence, type });

test("moving a trigger requires confirmation and cancellation changes neither page", () => {
  const profile = pages();
  assert.equal(triggers.assign(profile, "crew", enter, () => false), false);
  assert.deepEqual(profile[0].trigger_events, [enter]);
  assert.deepEqual(profile[1].trigger_events, [leave]);
  assert.equal(triggers.assign(profile, "crew", enter, (oldPage, newPage) => oldPage.id === "flight" && newPage.id === "crew"), true);
  assert.equal(profile[0].trigger_events.length, 0);
  assert.deepEqual(Array.from(profile[1].trigger_events), [leave, enter]);
  triggers.validate(profile, [enter, leave]);
});

test("legacy manual pages remain valid and unknown or duplicate triggers are rejected", () => {
  triggers.validate([{ id: "manual" }], [enter, leave]);
  assert.throws(() => triggers.validate([{ trigger_events: null }], [enter]), /list/);
  assert.throws(() => triggers.validate([{ trigger_events: ["unknown"] }], [enter]), /Unknown/);
  assert.throws(() => triggers.validate([{ trigger_events: [enter, enter] }], [enter]), /only one/);
  assert.throws(() => triggers.validate([{ trigger_events: [enter] }, { trigger_events: [enter] }], [enter]), /only one/);
});

test("first connection and browser reconnect baseline history instead of replaying it", () => {
  const cursor = triggers.createCursor();
  assert.equal(cursor.target(snapshot([event(1)]), pages(), true), null);
  assert.equal(cursor.target(snapshot([event(1), event(2, leave)]), pages(), true).page.id, "crew");
  cursor.reset();
  assert.equal(cursor.target(snapshot([event(1), event(2, leave), event(3)]), pages(), true), null);
  assert.equal(cursor.target(snapshot([event(3), event(4)]), pages(), true).event.sequence, 4);
});

test("repeated snapshots do not switch twice but identical new event types do", () => {
  const cursor = triggers.createCursor();
  cursor.target(snapshot([]), pages(), true);
  assert.equal(cursor.target(snapshot([event(1)]), pages(), true).page.id, "flight");
  assert.equal(cursor.target(snapshot([event(1)]), pages(), true), null);
  assert.equal(cursor.target(snapshot([event(1), event(2)]), pages(), true).event.sequence, 2);
});

test("events consumed while editing, busy or inactive are not replayed afterwards", () => {
  const cursor = triggers.createCursor();
  cursor.target(snapshot([]), pages(), true);
  assert.equal(cursor.target(snapshot([event(1)]), pages(), false), null);
  assert.equal(cursor.target(snapshot([event(1)]), pages(), true), null);
  assert.equal(cursor.target(snapshot([event(1), event(2, leave)]), pages(), true).page.id, "crew");
});

test("the last matching event wins, active profile mappings apply and new game streams reset numbering", () => {
  const cursor = triggers.createCursor();
  cursor.target(snapshot([]), pages(), true);
  assert.equal(cursor.target(snapshot([event(1), event(2, leave), event(3, "unknown")]), pages(), true).page.id, "crew");
  assert.equal(cursor.target(snapshot([event(1)], "next-game"), pages(), true).page.id, "flight");
  assert.equal(cursor.target(snapshot([event(1), event(2, leave)], "next-game"), [{ id: "other", trigger_events: [leave] }], true).page.id, "other");
});

test("capped history uses the retained new events and malformed identities are rejected", () => {
  const cursor = triggers.createCursor();
  cursor.target(snapshot([]), pages(), true);
  assert.equal(cursor.target(snapshot([event(101, leave), event(102)]), pages(), true).event.sequence, 102);
  assert.throws(() => cursor.target(snapshot([event(102), event(101)]), pages(), true), /sequence/);
});

test("page event heads retain a trigger even after it leaves the bounded history", () => {
  const cursor = triggers.createCursor();
  cursor.target({stream_id: "game", events: [], page_events: []}, pages(), true);
  const update = { stream_id: "game", events: [event(101, "GAME_EVENT_TYPE_ACTOR_SPAWN")],
    page_events: [event(1, leave), event(101, "GAME_EVENT_TYPE_ACTOR_SPAWN")] };
  assert.equal(cursor.target(update, pages(), true).page.id, "crew");
  assert.equal(cursor.target(update, pages(), true), null);
});
