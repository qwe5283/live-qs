import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  createEventValidator,
  loadEventRegistry,
  loadEventUnionReferences,
} from "../src/event-validator.mjs";

const contractsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const validDirectory = path.join(contractsRoot, "examples", "valid");
const invalidDirectory = path.join(contractsRoot, "examples", "invalid");

function readExamples(directory) {
  return fs.readdirSync(directory)
    .filter((name) => name.endsWith(".json"))
    .sort()
    .map((name) => ({ name, value: JSON.parse(fs.readFileSync(path.join(directory, name), "utf8")) }));
}

test("event registry exposes the first V1 activity interval", () => {
  assert.deepEqual(
    loadEventRegistry().map(({ eventType, schemaVersion }) => ({ eventType, schemaVersion })),
    [{ eventType: "activity.interval", schemaVersion: 1 }],
  );
});

test("event union references every registered event schema exactly once", () => {
  const expected = loadEventRegistry().map(({ fileName }) => `./events/${fileName}`);
  assert.deepEqual(loadEventUnionReferences().sort(), expected.sort());
});

test("every event schema has a valid example and multiple invalid examples", () => {
  const registry = loadEventRegistry();
  const validNames = readExamples(validDirectory).map(({ name }) => name);
  const invalidNames = readExamples(invalidDirectory).map(({ name }) => name);

  for (const { eventType, schemaVersion } of registry) {
    const prefix = `${eventType}.v${schemaVersion}.`;
    assert.ok(validNames.some((name) => name.startsWith(prefix)), `${prefix} needs a valid example`);
    assert.ok(
      invalidNames.filter((name) => name.startsWith(prefix)).length >= 3,
      `${prefix} needs at least three invalid examples`,
    );
  }
});

test("all valid event examples satisfy their registered schema and semantics", () => {
  const validate = createEventValidator();
  for (const example of readExamples(validDirectory)) {
    assert.deepEqual(validate(example.value), { valid: true, errors: [] }, example.name);
  }
});

test("invalid examples are rejected with their declared stable error code", () => {
  const validate = createEventValidator();
  for (const example of readExamples(invalidDirectory)) {
    const expectedCode = example.name.split(".").at(-2);
    const result = validate(example.value);
    assert.equal(result.valid, false, example.name);
    assert.equal(result.errors[0]?.code, expectedCode, example.name);
  }
});
