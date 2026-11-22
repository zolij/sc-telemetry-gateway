const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "../../src/ScTelemetryGateway.Windows/mfd/assets/device-identity.js"), "utf8");

function identityFixture(initialValue) {
  const values = new Map(initialValue ? [["mfd-device-id", initialValue]] : []);
  let generated = 0;
  const context = {
    window: {},
    localStorage: {
      getItem: key => values.get(key) ?? null,
      setItem: (key, value) => values.set(key, value)
    },
    crypto: {
      getRandomValues(bytes) {
        generated++;
        bytes.set(Array.from({ length: bytes.length }, (_, index) => index));
        return bytes;
      }
    }
  };
  vm.runInNewContext(source, context);
  return {
    get: () => context.window.MfdDeviceIdentity.getOrCreate(),
    stored: () => values.get("mfd-device-id"),
    generated: () => generated
  };
}

test("generates and persists a stable version-4 browser identifier", () => {
  const identity = identityFixture();
  const first = identity.get();
  assert.equal(first, "00010203-0405-4607-8809-0a0b0c0d0e0f");
  assert.equal(identity.stored(), first);
  assert.equal(identity.get(), first);
  assert.equal(identity.generated(), 1);
});

test("reuses a saved identifier and replaces malformed values", () => {
  const existing = identityFixture("123e4567-e89b-42d3-a456-426614174000");
  assert.equal(existing.get(), "123e4567-e89b-42d3-a456-426614174000");
  assert.equal(existing.generated(), 0);

  const invalid = identityFixture("not-a-device-id");
  assert.equal(invalid.get(), "00010203-0405-4607-8809-0a0b0c0d0e0f");
  assert.notEqual(invalid.stored(), "not-a-device-id");
});
