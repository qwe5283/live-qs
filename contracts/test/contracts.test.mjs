import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import { parse as parseYaml } from "yaml";
import {
  createEventValidator,
  loadEventRegistry,
  loadEventUnionReferences,
} from "../src/event-validator.mjs";
import { buildProtocolModelSchema } from "../src/protocol-model-schema.mjs";

const contractsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const validDirectory = path.join(contractsRoot, "examples", "valid");
const invalidDirectory = path.join(contractsRoot, "examples", "invalid");

function readExamples(directory) {
  return fs.readdirSync(directory)
    .filter((name) => name.endsWith(".json"))
    .sort()
    .map((name) => ({ name, value: JSON.parse(fs.readFileSync(path.join(directory, name), "utf8")) }));
}

test("event registry exposes the registered V1 event types", () => {
  assert.deepEqual(
    loadEventRegistry().map(({ eventType, schemaVersion }) => ({ eventType, schemaVersion })),
    [
      { eventType: "activity.interval", schemaVersion: 1 },
      { eventType: "health.heartrate.sample", schemaVersion: 1 },
      { eventType: "health.sleep.session", schemaVersion: 1 },
      { eventType: "health.step.sample", schemaVersion: 1 },
    ],
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

test("batch response acknowledges every item and explains rejections", () => {
  const ajv = new Ajv2020({ strict: true });
  addFormats(ajv);
  for (const name of ["schemas/event-envelope.v1.schema.json", "schemas/event.schema.json"]) {
    ajv.addSchema(JSON.parse(fs.readFileSync(path.join(contractsRoot, name), "utf8")));
  }
  for (const { schema } of loadEventRegistry()) {
    ajv.addSchema(schema);
  }

  const openApi = parseYaml(fs.readFileSync(path.join(contractsRoot, "openapi.yaml"), "utf8"));
  const protocolSchema = buildProtocolModelSchema(openApi);
  ajv.addSchema(protocolSchema);
  const validate = ajv.compile({ $ref: `${protocolSchema.$id}#/$defs/EventBatchResponse` });
  const rejected = {
    event_id: "018f62d6-4f34-7c82-9085-57c8af1d7a44",
    revision: 1,
    status: "rejected",
  };

  assert.equal(validate({ results: [] }), false);
  assert.equal(validate({ results: [rejected] }), false);
  assert.equal(validate({
    results: [{ ...rejected, error: { code: "invalid_unit", message: "Unit is invalid." } }],
  }), true);
});
