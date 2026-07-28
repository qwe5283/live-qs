# Repository Guidelines

## V1 Source of Truth

`.scratch/live-qs-v1/spec.md` is the authoritative V1 SPEC. Read available `CONTEXT.md` and relevant ADRs under `docs/adr/`. OpenAPI 3.1 and versioned JSON Schema under `contracts/` define the protocol. Treat legacy code as reference, never requirements.

## Project Structure

This is one Monorepo whose components version independently:

- `contracts/`: OpenAPI, event schemas, sanitized examples.
- `server/`: Express/TypeScript/MongoDB.
- `web/`: Vue 3/Vite.
- `windows/`: .NET 9/WPF and SQLite.
- `android/`: Kotlin/Compose.
- `server_deprecated/`, `windows_deprecated/`: read-only references; do not extend them or preserve their contracts.
- `.scratch/`: local specifications and tickets.

Do not edit generated `build/`, `bin/`, `obj/`, `dist/`, or `artifacts/` content.

## Development Workflow

Implement in this order: contracts, server, Windows, WebUI, Android, then the read-only AI Skill. Tie each change to the SPEC or a ticket under `.scratch/live-qs-v1/issues/`.

Preserve proven Win32 sampling, Windows SQLite and tray lifecycle, Android permissions, UsageStats, Health Connect, and notification listening. Replace legacy protocol and synchronization boundaries.

Run all checks with `npm run check`, or target one component:

- `cd server; npm run typecheck; npm test; npm run build`
- `cd web; npm run build`
- `cd windows; dotnet test LiveQs.Windows.sln`
- `cd android; .\gradlew.bat lint test assembleDebug`

## Architecture, Security, and Testing

V1 is single-Owner, and MongoDB is authoritative for cross-device queries. Historical events require stable IDs, versioned schemas, revisions, and per-item acknowledgements; heartbeats never contribute directly to history. Raw titles, executable paths, and notification bodies stay on-device. AI credentials are scoped and read-only. Never commit passwords, tokens, databases, `local.properties`, or `.env` files.

Use strict TypeScript with two spaces, double quotes, and semicolons; official Kotlin style; and four-space C# with nullable types and file-scoped namespaces. Name tests `*.test.ts` or `*Tests.cs`.

Test observable behavior. The primary automated seam is HTTP against real MongoDB. Retain minimal contract, authorization, idempotency/revision, interval, timezone, classification, and synchronization tests. Use real-device smoke tests and the seven-day acceptance run for platform behavior.

## Changes and Review

Keep commits focused and imperative. Reference the spec or ticket, list verification commands, and include screenshots for visible changes. Update the SPEC before scope, contracts before integrations, and ADRs before reversing architecture.

## Agent skills

### Issue tracker

Issues and specs are tracked as Local Markdown under `.scratch/`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default canonical triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

Use the single-context layout: root `CONTEXT.md` and system-wide ADRs under `docs/adr/`. See `docs/agents/domain.md`.
