# Domain Docs

## Before Exploring

Read `CONTEXT.md` at the repository root and ADRs relevant to the work under `docs/adr/`. If either is absent, continue silently.

## Layout

This repository uses a single-context layout:

- `CONTEXT.md`: product domain model and shared vocabulary.
- `docs/adr/`: system-wide architectural decisions.

## Vocabulary

Use terms exactly as defined in `CONTEXT.md` in specifications, tickets, tests, and implementation. If a required concept is missing, record it as a domain-modeling gap instead of inventing competing terminology.

## ADR Conflicts

If proposed work contradicts an ADR, identify the conflicting ADR explicitly and explain why it should be reconsidered.
