#!/usr/bin/env node
// Generates one component's update manifest (Release Asset) from release
// facts. The artifact's SHA-256 is computed here so the manifest can never
// disagree with the bytes it publishes. Used by the component release
// workflows (ADR-0002); the Owner's operational steps live in ticket
// .scratch/live-qs-v1/issues/17-component-release-channels.md.

import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { buildManifest } from "./manifest.mjs";

function usage() {
  return `Usage: node generate-manifest.mjs --component <windows|android> --version X.Y.Z
  --download-url <absolute http(s) URL> --min-compatible X.Y.Z --artifact <file>
  [--released-at <UTC ISO instant>] [--out <file>]

Defaults: --released-at is the current UTC instant; --out is
liveqs-<component>-update.json in the working directory.`;
}

function parseArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) {
    const flag = argv[index];
    const value = argv[index + 1];
    if (!flag.startsWith("--") || value === undefined) {
      throw new Error(`Malformed argument near "${flag ?? ""}".\n${usage()}`);
    }
    values[flag.slice(2)] = value;
  }
  return values;
}

const args = parseArguments(process.argv.slice(2));
const requiredFlags = ["component", "version", "download-url", "min-compatible", "artifact"];
for (const flag of requiredFlags) {
  if (!args[flag]) {
    console.error(`Missing required --${flag}.\n${usage()}`);
    process.exit(1);
  }
}

try {
  const manifest = buildManifest({
    component: args.component,
    version: args.version,
    releasedAt: args["released-at"] ?? new Date().toISOString().replace(/\.\d{3}Z$/, "Z"),
    downloadUrl: args["download-url"],
    minCompatibleVersion: args["min-compatible"],
    artifactPath: args.artifact,
  });
  const outPath = args.out ?? `liveqs-${args.component}-update.json`;
  fs.writeFileSync(outPath, `${JSON.stringify(manifest, null, 2)}\n`);
  console.log(`Wrote ${path.resolve(outPath)} (sha256 ${manifest.sha256}).`);
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}
