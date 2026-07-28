import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const contractsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const eventSchemaDirectory = path.join(contractsRoot, "schemas", "events");

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

export function loadEventRegistry() {
  const files = fs.readdirSync(eventSchemaDirectory)
    .filter((name) => name.endsWith(".schema.json"))
    .sort();

  return files.map((name) => {
    const schema = readJson(path.join(eventSchemaDirectory, name));
    return {
      eventType: schema.properties?.event_type?.const,
      schemaVersion: schema.properties?.schema_version?.const,
      fileName: name,
      schema,
    };
  });
}

export function loadEventUnionReferences() {
  const schema = readJson(path.join(contractsRoot, "schemas", "event.schema.json"));
  return schema.oneOf.map((entry) => entry.$ref);
}

export function createEventValidator() {
  const ajv = new Ajv2020({ allErrors: true, strict: true });
  addFormats(ajv);

  const envelopePath = path.join(contractsRoot, "schemas", "event-envelope.v1.schema.json");
  const envelope = readJson(envelopePath);
  ajv.addSchema(envelope);

  const registry = loadEventRegistry();
  const validators = new Map();
  for (const entry of registry) {
    ajv.addSchema(entry.schema);
    validators.set(`${entry.eventType}@${entry.schemaVersion}`, ajv.getSchema(entry.schema.$id));
  }
  const eventUnion = readJson(path.join(contractsRoot, "schemas", "event.schema.json"));
  ajv.addSchema(eventUnion);
  const validateRegisteredEvent = ajv.getSchema(eventUnion.$id);

  return (event) => {
    const versions = registry
      .filter((entry) => entry.eventType === event?.event_type)
      .map((entry) => entry.schemaVersion);

    if (versions.length === 0) {
      return invalid("unknown_event_type", "/event_type", "Event type is not registered.");
    }
    if (!versions.includes(event?.schema_version)) {
      return invalid("unknown_schema_version", "/schema_version", "Schema version is not registered for this event type.");
    }

    for (const field of ["start_at", "end_at"]) {
      if (typeof event?.[field] === "string" && !event[field].endsWith("Z")) {
        return invalid("invalid_timestamp", `/${field}`, "Event timestamps must use UTC Z notation.");
      }
    }

    const validate = validators.get(`${event.event_type}@${event.schema_version}`);
    if (!validate || !validateRegisteredEvent(event)) {
      const errors = validateRegisteredEvent.errors ?? validate?.errors ?? [];
      const unitError = errors.find((error) => error.instancePath.endsWith("/unit"));
      return invalid(
        unitError ? "invalid_unit" : "schema_invalid",
        unitError?.instancePath ?? errors[0]?.instancePath ?? "",
        unitError?.message ?? errors[0]?.message ?? "Event does not match its schema.",
      );
    }

    try {
      new Intl.DateTimeFormat("en-US", { timeZone: event.capture_timezone }).format();
    } catch {
      return invalid("invalid_timezone", "/capture_timezone", "Capture timezone must be an IANA timezone.");
    }

    const expectedOffset = timezoneOffsetMinutes(event.start_at, event.capture_timezone);
    if (event.capture_offset_minutes !== expectedOffset) {
      return invalid(
        "invalid_offset",
        "/capture_offset_minutes",
        "Capture offset must match the capture timezone at the event start.",
      );
    }

    if (event.end_at !== undefined) {
      const start = Date.parse(event.start_at);
      const end = Date.parse(event.end_at);
      if (end <= start) {
        return invalid("invalid_time_range", "/end_at", "End must be later than start.");
      }
      if (event.finalization_state === "final" && event.payload.duration.value !== end - start) {
        return invalid("duration_mismatch", "/payload/duration/value", "Duration must match the event time range.");
      }
    }

    return { valid: true, errors: [] };
  };
}

function invalid(code, instancePath, message) {
  return { valid: false, errors: [{ code, instancePath, message }] };
}

function timezoneOffsetMinutes(timestamp, timezone) {
  const name = new Intl.DateTimeFormat("en-US", {
    timeZone: timezone,
    timeZoneName: "longOffset",
  }).formatToParts(new Date(timestamp)).find((part) => part.type === "timeZoneName")?.value;
  const match = /^GMT(?:([+-])(\d{2}):(\d{2}))?$/.exec(name ?? "");
  if (!match || !match[1]) {
    return 0;
  }

  const magnitude = Number(match[2]) * 60 + Number(match[3]);
  return match[1] === "-" ? -magnitude : magnitude;
}
