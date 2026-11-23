const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "../../src/VerseDataGateway.Windows/mfd/assets/wake-lock.js"), "utf8");

function fixture({ request, play } = {}) {
  const listeners = new Map(), players = [], messages = [];
  const document = {
    visibilityState: "visible",
    addEventListener(type, handler) { listeners.set(type, handler); },
    body: { append(player) { players.push(player); } },
    createElement(type) {
      assert.equal(type, "video");
      const callbacks = new Map();
      return {
        attributes: {}, paused: false, removed: false,
        setAttribute(name, value) { this.attributes[name] = value; },
        addEventListener(type, handler) { callbacks.set(type, handler); },
        play() { return play ? play(this) : Promise.resolve(); },
        pause() { this.paused = true; callbacks.get("pause")?.(); },
        remove() { this.removed = true; },
        emit(type) { callbacks.get(type)?.(); }
      };
    }
  };
  const navigator = request ? { wakeLock: { request } } : {};
  const wake = vm.runInNewContext(source + "\nMfdWakeLock;", { document, navigator });
  wake.init(message => messages.push(message));
  return {
    wake, document, players, messages,
    visibility(value) {
      document.visibilityState = value;
      listeners.get("visibilitychange")();
    }
  };
}

function lockFixture() {
  let releases = 0, onRelease;
  return {
    addEventListener(type, callback) { if (type === "release") onRelease = callback; },
    release() { releases++; onRelease?.(); return Promise.resolve(); },
    released() { return releases; }
  };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test("native wake lock starts on focus, is reacquired after visibility changes and released on exit", async () => {
  const locks = [];
  const view = fixture({ request: type => {
    assert.equal(type, "screen");
    const lock = lockFixture();
    locks.push(lock);
    return Promise.resolve(lock);
  } });
  view.wake.start();
  assert.equal(locks.length, 1);
  await flush();
  view.visibility("hidden");
  assert.equal(locks[0].released(), 1);
  view.visibility("visible");
  assert.equal(locks.length, 2);
  await flush();
  view.wake.stop();
  assert.equal(locks[1].released(), 1);
  assert.equal(view.players.length, 0);
});

test("late native lock is released after focus has ended", async () => {
  let resolve;
  const view = fixture({ request: () => new Promise(done => { resolve = done; }) });
  view.wake.start();
  view.wake.stop();
  const lock = lockFixture();
  resolve(lock);
  await flush();
  assert.equal(lock.released(), 1);
});

test("HTTP fallback plays a local inline muted video and stops it on exit", async () => {
  const view = fixture();
  view.wake.start();
  const player = view.players[0];
  assert.equal(player.src, "/assets/keep-awake.mp4");
  assert.equal(player.muted, true);
  assert.equal(player.playsInline, true);
  assert.equal(player.loop, true);
  await flush();
  view.wake.stop();
  assert.equal(player.paused, true);
  assert.equal(player.removed, true);
});

test("failed or interrupted playback and denied native requests report a warning", async () => {
  const failed = fixture({ play: () => Promise.reject(new Error("blocked")) });
  failed.wake.start();
  await flush();
  assert.match(failed.messages.at(-1), /Could not keep the screen awake/);

  const paused = fixture();
  paused.wake.start();
  await flush();
  paused.players[0].pause();
  assert.match(paused.messages.at(-1), /Could not keep the screen awake/);

  const denied = fixture({ request: () => Promise.reject(new Error("denied")) });
  denied.wake.start();
  await flush();
  assert.match(denied.messages.at(-1), /Could not keep the screen awake/);
});

test("a late fallback playback is stopped after exit", async () => {
  let resolve;
  const view = fixture({ play: () => new Promise(done => { resolve = done; }) });
  view.wake.start();
  const player = view.players[0];
  view.wake.stop();
  player.paused = false;
  resolve();
  await flush();
  assert.equal(player.paused, true);
});
