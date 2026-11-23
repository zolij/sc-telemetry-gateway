const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../../src/VerseDataGateway.Windows/mfd/assets/presentation.js"), "utf8");
const presentation = vm.runInNewContext(source + "\nMfdPresentation;", {
  Intl: { NumberFormat: class extends Intl.NumberFormat {
    constructor(locale, options) { super(locale ?? "en-US", options); }
  } }
});

test("fraction, already-percent and current/max values produce the same percentage", () => {
  assert.equal(presentation.format(0.75, 2, "percent_fraction"), "75%");
  assert.equal(presentation.format(75, 2, "percent"), "75%");
  assert.equal(presentation.format(150, 2, "percent_max", 200), "75%");
  assert.equal(presentation.format(0.75555, 2, "percent_fraction"), "75.56%");
  assert.equal(presentation.format(0, 2, "percent_fraction"), "0%");
  assert.equal(presentation.format(1, 2, "percent_fraction"), "100%");
  assert.equal(presentation.format(1.2, 2, "percent_fraction"), "120%");
  assert.equal(presentation.format(0.9927, 2, "percent_fraction"), "99.27%");
});

test("missing, invalid and mismatched maxima never produce a false percentage", () => {
  for (const maximum of [null, undefined, 0, -1, NaN, Infinity, "100"])
    assert.equal(presentation.format(75, 2, "percent_max", maximum), "Unavailable");
  for (const value of [null, undefined, NaN, Infinity])
    assert.equal(presentation.format(value, 2, "percent_fraction"), "Unknown");
  assert.equal(presentation.format(1, 2, "percent_max", Number.MIN_VALUE), "Unavailable");
  const current = { value: 150, updated_at: "2026-11-22T10:00:00Z", last_known: false };
  const maximum = { value: 200, updated_at: current.updated_at, last_known: false };
  assert.equal(presentation.maximumValue(current, maximum), 200);
  for (const other of [null, { ...maximum, updated_at: "2026-11-22T09:59:00Z" }, { ...maximum, last_known: true }])
    assert.equal(presentation.format(current.value, 2, "percent_max", presentation.maximumValue(current, other)), "Unavailable");
});

test("lists format each percentage without inventing missing values", () => {
  assert.equal(presentation.format([0.25, 0.5, null, 1], 0, "percent_fraction"), "25% / 50% / Unknown / 100%");
  assert.equal(presentation.format([], 2, "percent_fraction"), "None");
});

test("raw override preserves numbers, large integer strings, booleans and enum labels", () => {
  assert.equal(presentation.format(0.75, 2, "number"), "0.75");
  assert.equal(presentation.format(135.255, 2), "135.26");
  assert.equal(presentation.format("9223372036854775807", 2), "9223372036854775807");
  assert.equal(presentation.format(false, 2), "No");
  assert.equal(presentation.format("VEHICLE_MASTER_MODE_SCM", 2, "number", null, "enum"), "Scm");
});

test("string fields are preserved and empty strings differ from missing values", () => {
  for (const value of ["ABC_DEF", "RSI", "VEHICLE_MASTER_MODE_SCM", "Mixed Case"])
    assert.equal(presentation.format(value, 2, "number", null, "string"), value);
  assert.equal(presentation.format("", 2, "number", null, "string"), "Empty");
  assert.equal(presentation.format(null, 2, "number", null, "string"), "Unknown");
  assert.equal(presentation.format(["ABC_DEF", ""], 2, "number", null, "string"), "ABC_DEF / Empty");
  assert.equal(presentation.format(["GAME_EVENT_TYPE_ACTOR_SPAWN", "99"], 2, "number", null, "enum"), "Actor Spawn / 99");
});
