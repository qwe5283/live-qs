// Shared release-update-manifest logic for the LiveQs component release
// channels (ADR-0002). One manifest per component is published as a Release
// Asset; clients parse, validate, and evaluate it against their own version.
// The same evaluation semantics are mirrored in the Windows (C#) and Android
// (Kotlin) clients; the known-vector tests in this directory and in the two
// client test suites pin all three implementations to identical decisions.

import crypto from "node:crypto";
import fs from "node:fs";

export const MANIFEST_VERSION = 1;

// Core semver X.Y.Z without pre-release or build metadata: V1 release
// channels use plain semantic versions only.
const SEMVER_PATTERN = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const COMPONENT_PATTERN = /^[a-z][a-z0-9-]*$/;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const ISO_INSTANT_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/;

const MANIFEST_FIELDS = [
  "manifest_version",
  "component",
  "version",
  "released_at",
  "download_url",
  "sha256",
  "min_compatible_version",
];

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isAbsoluteHttpUrl(value) {
  if (typeof value !== "string") return false;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:";
  } catch {
    return false;
  }
}

export function compareSemver(left, right) {
  const a = SEMVER_PATTERN.exec(left);
  const b = SEMVER_PATTERN.exec(right);
  if (!a || !b) throw new Error(`Not a core semver version: ${!a ? left : right}`);
  for (let index = 0; index < 3; index += 1) {
    const leftPart = Number.parseInt(a[index + 1], 10);
    const rightPart = Number.parseInt(b[index + 1], 10);
    if (leftPart !== rightPart) return leftPart < rightPart ? -1 : 1;
  }
  return 0;
}

/** Validates the manifest shape; applicability (component match, freshness) is the client's decision. */
export function validateManifestObject(value) {
  const errors = [];
  if (!isPlainObject(value)) {
    return { ok: false, manifest: null, errors: ["manifest must be a JSON object"] };
  }
  const unknown = Object.keys(value).filter((key) => !MANIFEST_FIELDS.includes(key));
  if (unknown.length > 0) errors.push(`unknown fields: ${unknown.sort().join(", ")}`);
  for (const field of MANIFEST_FIELDS) {
    if (!(field in value)) errors.push(`missing field: ${field}`);
  }
  if (errors.length > 0) return { ok: false, manifest: null, errors };

  if (value.manifest_version !== MANIFEST_VERSION) {
    errors.push(`manifest_version must be ${MANIFEST_VERSION}`);
  }
  if (typeof value.component !== "string" || !COMPONENT_PATTERN.test(value.component)) {
    errors.push("component must be a lowercase [a-z][a-z0-9-]* identifier");
  }
  if (typeof value.version !== "string" || !SEMVER_PATTERN.test(value.version)) {
    errors.push("version must be core semver X.Y.Z");
  }
  if (
    typeof value.min_compatible_version !== "string" ||
    !SEMVER_PATTERN.test(value.min_compatible_version)
  ) {
    errors.push("min_compatible_version must be core semver X.Y.Z");
  }
  if (typeof value.released_at !== "string" || !ISO_INSTANT_PATTERN.test(value.released_at) ||
    Number.isNaN(Date.parse(value.released_at))) {
    errors.push("released_at must be a UTC ISO-8601 instant (YYYY-MM-DDTHH:MM:SSZ)");
  }
  if (!isAbsoluteHttpUrl(value.download_url)) {
    errors.push("download_url must be an absolute http(s) URL");
  }
  if (typeof value.sha256 !== "string" || !SHA256_PATTERN.test(value.sha256)) {
    errors.push("sha256 must be a lowercase 64-hex digest");
  }
  if (errors.length > 0) return { ok: false, manifest: null, errors };

  if (compareSemver(value.min_compatible_version, value.version) > 0) {
    return { ok: false, manifest: null, errors: ["min_compatible_version must not exceed version"] };
  }
  return { ok: true, manifest: value, errors: [] };
}

/**
 * Decides whether one manifest describes an update this client should offer.
 * Every refusal carries a stable code so the failure is diagnosable; a
 * manifest published for another component is never an update for this one.
 */
export function evaluateUpdate({ component, currentVersion }, manifest) {
  const validation = validateManifestObject(manifest);
  if (!validation.ok) {
    return { decision: "refuse", code: "manifest_parse_failed", detail: validation.errors.join("; ") };
  }
  const parsed = validation.manifest;
  if (parsed.component !== component) {
    return {
      decision: "refuse",
      code: "manifest_component_mismatch",
      detail: `manifest is for component "${parsed.component}", not "${component}"`,
    };
  }
  if (compareSemver(parsed.version, currentVersion) <= 0) {
    return { decision: "up-to-date", code: "manifest_version_not_newer" };
  }
  if (compareSemver(currentVersion, parsed.min_compatible_version) < 0) {
    return {
      decision: "refuse",
      code: "min_compatible_not_met",
      detail: `running ${currentVersion} predates min_compatible_version ${parsed.min_compatible_version}; update manually through ${parsed.min_compatible_version} first`,
    };
  }
  return {
    decision: "available",
    version: parsed.version,
    released_at: parsed.released_at,
    download_url: parsed.download_url,
    sha256: parsed.sha256,
    min_compatible_version: parsed.min_compatible_version,
  };
}

function sha256File(artifactPath) {
  return crypto.createHash("sha256").update(fs.readFileSync(artifactPath)).digest("hex");
}

/** Builds and self-validates one component's update manifest from release facts. */
export function buildManifest({
  component,
  version,
  releasedAt,
  downloadUrl,
  minCompatibleVersion,
  artifactPath,
}) {
  const candidate = {
    manifest_version: MANIFEST_VERSION,
    component,
    version,
    released_at: releasedAt,
    download_url: downloadUrl,
    sha256: sha256File(artifactPath),
    min_compatible_version: minCompatibleVersion,
  };
  const validation = validateManifestObject(candidate);
  if (!validation.ok) {
    throw new Error(`invalid release manifest input: ${validation.errors.join("; ")}`);
  }
  return validation.manifest;
}
