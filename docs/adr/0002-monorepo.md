# ADR-0002: One Monorepo With Independent Component Versions

Status: Accepted

## Context

Windows, Android, Web, and Server must evolve together around one versioned protocol. Separate repositories make those coordinated changes non-atomic.

## Decision

Maintain all active components in one root repository. Components retain independent versions, release tags, assets, and update manifests.

## Consequences

Root tooling must preserve component-local build commands and path-aware checks. Component release cadence remains independent without separate source repositories.
