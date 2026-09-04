import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  buildManifest,
  compareSemver,
  evaluateUpdate,
  validateManifestObject,
} from "./manifest.mjs";

const releaseRoot = path.dirname(fileURLToPath(import.meta.url));
const generatorPath = path.join(releaseRoot, "generate-manifest.mjs");

// NIST known vector for the empty string.
const EMPTY_SHA256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

function validManifest(overrides = {}) {
  return {
    manifest_version: 1,
    component: "windows",
    version: "0.2.0",
    released_at: "2026-09-04T08:00:00Z",
    download_url: "https://github.com/qwe5283/live-qs/releases/download/windows%2Fv0.2.0/LiveQs.Windows-0.2.0-win-x64.zip",
    sha256: EMPTY_SHA256,
    min_compatible_version: "0.1.0",
    ...overrides,
  };
}

test("compareSemver orders core semver numerically, not lexically", () => {
  assert.equal(compareSemver("0.1.0", "0.2.0"), -1);
  assert.equal(compareSemver("0.2.0", "0.1.0"), 1);
  assert.equal(compareSemver("1.0.0", "1.0.0"), 0);
  assert.equal(compareSemver("0.10.0", "0.9.0"), 1);
  assert.equal(compareSemver("1.2.3", "1.2.10"), -1);
  assert.equal(compareSemver("10.0.0", "9.99.99"), 1);
});

test("evaluateUpdate offers a strictly newer applicable release", () => {
  const decision = evaluateUpdate(
    { component: "windows", currentVersion: "0.1.0" },
    validManifest(),
  );
  assert.equal(decision.decision, "available");
  assert.equal(decision.version, "0.2.0");
  assert.equal(decision.sha256, EMPTY_SHA256);
  assert.ok(decision.download_url.includes("windows%2Fv0.2.0"));
});

test("evaluateUpdate never offers a downgrade or re-install of the same version", () => {
  const cases = [
    { version: "0.1.0", min_compatible_version: "0.1.0" },
    { version: "0.0.9", min_compatible_version: "0.0.9" },
  ];
  for (const override of cases) {
    const decision = evaluateUpdate(
      { component: "windows", currentVersion: "0.1.0" },
      validManifest(override),
    );
    assert.deepEqual(decision, { decision: "up-to-date", code: "manifest_version_not_newer" });
  }
});

test("evaluateUpdate refuses a manifest published for another component", () => {
  // Isolation property: a release published on another component's channel
  // must never look like an update for this client.
  const decision = evaluateUpdate(
    { component: "windows", currentVersion: "0.1.0" },
    validManifest({ component: "android", version: "99.0.0" }),
  );
  assert.equal(decision.decision, "refuse");
  assert.equal(decision.code, "manifest_component_mismatch");
});

test("evaluateUpdate refuses when the running client predates the minimum compatible version", () => {
  const decision = evaluateUpdate(
    { component: "windows", currentVersion: "0.0.9" },
    validManifest({ min_compatible_version: "0.1.0" }),
  );
  assert.equal(decision.decision, "refuse");
  assert.equal(decision.code, "min_compatible_not_met");
});

test("evaluateUpdate accepts a client exactly at the minimum compatible version", () => {
  const decision = evaluateUpdate(
    { component: "windows", currentVersion: "0.1.0" },
    validManifest({ min_compatible_version: "0.1.0" }),
  );
  assert.equal(decision.decision, "available");
});

test("evaluateUpdate refuses structurally invalid manifests with a diagnosable code", () => {
  const invalidCases = [
    validManifest({ manifest_version: 2 }),
    validManifest({ version: "0.2" }),
    validManifest({ version: "01.2.0" }),
    validManifest({ sha256: "not-a-hash" }),
    validManifest({ download_url: "ftp://example.com/a.zip" }),
    validManifest({ download_url: "not-a-url" }),
    validManifest({ released_at: "yesterday" }),
    validManifest({ min_compatible_version: "x.y.z" }),
    validManifest({ component: "" }),
    validManifest({ component: "Windows" }),
    validManifest({ unexpected_field: true }),
  ];
  for (const manifest of invalidCases) {
    const decision = evaluateUpdate(
      { component: "windows", currentVersion: "0.1.0" },
      manifest,
    );
    assert.equal(decision.decision, "refuse", JSON.stringify(manifest));
    assert.equal(decision.code, "manifest_parse_failed", JSON.stringify(manifest));
    assert.ok(decision.detail, "refusal must carry a diagnosable detail");
  }
});

test("evaluateUpdate refuses non-object manifests", () => {
  for (const manifest of [null, "text", 42, [], undefined]) {
    const decision = evaluateUpdate({ component: "windows", currentVersion: "0.1.0" }, manifest);
    assert.equal(decision.decision, "refuse");
    assert.equal(decision.code, "manifest_parse_failed");
  }
});

test("validateManifestObject reports which field failed", () => {
  const result = validateManifestObject(validManifest({ sha256: "XYZ" }));
  assert.equal(result.ok, false);
  assert.match(result.errors.join(" "), /sha256/);
});

test("buildManifest hashes the artifact and self-validates its output", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "liveqs-release-"));
  const artifactPath = path.join(directory, "LiveQs.Windows-0.2.0-win-x64.zip");
  fs.writeFileSync(artifactPath, "");

  const manifest = buildManifest({
    component: "windows",
    version: "0.2.0",
    releasedAt: "2026-09-04T08:00:00Z",
    downloadUrl: "https://example.com/LiveQs.Windows-0.2.0-win-x64.zip",
    minCompatibleVersion: "0.1.0",
    artifactPath,
  });

  assert.deepEqual(manifest, {
    manifest_version: 1,
    component: "windows",
    version: "0.2.0",
    released_at: "2026-09-04T08:00:00Z",
    download_url: "https://example.com/LiveQs.Windows-0.2.0-win-x64.zip",
    sha256: EMPTY_SHA256,
    min_compatible_version: "0.1.0",
  });
  assert.deepEqual(validateManifestObject(manifest), { ok: true, manifest, errors: [] });
});

test("buildManifest rejects impossible release inputs", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "liveqs-release-"));
  const artifactPath = path.join(directory, "artifact.zip");
  fs.writeFileSync(artifactPath, "");

  const cases = [
    { component: "Windows", version: "0.2.0", releasedAt: "2026-09-04T08:00:00Z", downloadUrl: "https://example.com/a", minCompatibleVersion: "0.1.0", artifactPath },
    { component: "windows", version: "0.2", releasedAt: "2026-09-04T08:00:00Z", downloadUrl: "https://example.com/a", minCompatibleVersion: "0.1.0", artifactPath },
    { component: "windows", version: "0.2.0", releasedAt: "2026-09-04", downloadUrl: "https://example.com/a", minCompatibleVersion: "0.1.0", artifactPath },
    { component: "windows", version: "0.2.0", releasedAt: "2026-09-04T08:00:00Z", downloadUrl: "/relative", minCompatibleVersion: "0.1.0", artifactPath },
    { component: "windows", version: "0.2.0", releasedAt: "2026-09-04T08:00:00Z", downloadUrl: "https://example.com/a", minCompatibleVersion: "0.3.0", artifactPath },
    { component: "windows", version: "0.2.0", releasedAt: "2026-09-04T08:00:00Z", downloadUrl: "https://example.com/a", minCompatibleVersion: "0.1.0", artifactPath: path.join(directory, "missing.zip") },
  ];
  for (const input of cases) {
    assert.throws(() => buildManifest(input), undefined, JSON.stringify(input));
  }
});

test("generate-manifest CLI writes a valid manifest next to the release assets", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "liveqs-release-"));
  const artifactPath = path.join(directory, "LiveQs.Android-0.2.0.apk");
  fs.writeFileSync(artifactPath, "");
  const outPath = path.join(directory, "liveqs-android-update.json");

  const result = spawnSync(process.execPath, [
    generatorPath,
    "--component", "android",
    "--version", "0.2.0",
    "--released-at", "2026-09-04T08:00:00Z",
    "--download-url", "https://example.com/LiveQs.Android-0.2.0.apk",
    "--min-compatible", "0.1.0",
    "--artifact", artifactPath,
    "--out", outPath,
  ], { encoding: "utf8" });

  assert.equal(result.status, 0, result.stderr);
  const manifest = JSON.parse(fs.readFileSync(outPath, "utf8"));
  assert.equal(manifest.component, "android");
  assert.equal(manifest.sha256, EMPTY_SHA256);
  const validation = validateManifestObject(manifest);
  assert.equal(validation.ok, true, validation.errors.join("; "));
});

test("generate-manifest CLI fails with a diagnosable message on bad input", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "liveqs-release-"));
  const artifactPath = path.join(directory, "artifact.zip");
  fs.writeFileSync(artifactPath, "");

  const result = spawnSync(process.execPath, [
    generatorPath,
    "--component", "windows",
    "--version", "not-semver",
    "--released-at", "2026-09-04T08:00:00Z",
    "--download-url", "https://example.com/a.zip",
    "--min-compatible", "0.1.0",
    "--artifact", artifactPath,
    "--out", path.join(directory, "out.json"),
  ], { encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /version/i);
});

test("generate-manifest CLI defaults the output name and release instant", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "liveqs-release-"));
  const artifactPath = path.join(directory, "artifact.zip");
  fs.writeFileSync(artifactPath, "");

  const result = spawnSync(process.execPath, [
    generatorPath,
    "--component", "windows",
    "--version", "0.2.0",
    "--download-url", "https://example.com/a.zip",
    "--min-compatible", "0.1.0",
    "--artifact", artifactPath,
  ], { encoding: "utf8", cwd: directory });

  assert.equal(result.status, 0, result.stderr);
  const outPath = path.join(directory, "liveqs-windows-update.json");
  assert.ok(fs.existsSync(outPath), "default --out must be liveqs-<component>-update.json in the working directory");
  const manifest = JSON.parse(fs.readFileSync(outPath, "utf8"));
  assert.match(manifest.released_at, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/);
});
