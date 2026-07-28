# LiveQs

LiveQs is a single-Owner quantified-self system for collecting activity, screen use, health, and spending facts from Windows and Android devices.

The V1 source of truth is `.scratch/live-qs-v1/spec.md`. Components share one repository while retaining independent versions and release channels.

## Checks

Run targeted checks from the repository root:

```powershell
npm run check:server
npm run check:web
npm run check:windows
npm run check:android
```

Use `npm run check` only when all local platform prerequisites are available.

## Components

- `server/`: Express and MongoDB API
- `web/`: Vue Owner dashboard
- `windows/`: WPF collector
- `android/`: Android collector
- `.scratch/`: local V1 specification and tickets

Deprecated directories are read-only references and are not API compatibility targets.
