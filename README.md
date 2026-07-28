# LiveQs

LiveQs is a single-Owner quantified-self system for collecting activity, screen use, health, and spending facts from Windows and Android devices.

The V1 source of truth is `.scratch/live-qs-v1/spec.md`. Components share one repository while retaining independent versions and release channels.

## Checks

Install Node dependencies within each component:

```powershell
npm --prefix server ci
npm --prefix web ci
```

Run all checks from the repository root:

```powershell
.\scripts\check.ps1
```

Pass `server`, `web`, `windows`, or `android` to check one component, for example
`.\scripts\check.ps1 server`. The repository root is not a Node package; each
component owns its dependencies, version, and release channel.

## Components

- `server/`: Express and MongoDB API
- `web/`: Vue Owner dashboard
- `windows/`: WPF collector
- `android/`: Android collector
- `.scratch/`: local V1 specification and tickets

Deprecated directories are read-only references and are not API compatibility targets.
