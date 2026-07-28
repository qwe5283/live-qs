# Issue Tracker: Local Markdown

Issues and specs live as Markdown files under `.scratch/`.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`.
- The spec is `.scratch/<feature-slug>/spec.md`.
- Implementation tickets live in `.scratch/<feature-slug>/issues/`.
- Use one numbered file per ticket, such as `01-define-contracts.md`.
- Record triage state with a `Status:` line while work is open. Completed tickets use `Status: resolved` and append an `## Answer` section.
- Append discussion under a `## Comments` heading.

## Publishing and Fetching

When a skill says “publish to the issue tracker,” create the appropriate file under `.scratch/<feature-slug>/`.

When a skill says “fetch the relevant ticket,” read the referenced local Markdown file.

## Wayfinding

For work created through Wayfinding, use `.scratch/<effort>/map.md` as the map. Its child tickets live under `.scratch/<effort>/issues/`, record `Type:`, `Status:`, and optional `Blocked by:` fields, and are resolved by appending an `## Answer` section. Tickets created directly by `to-tickets` do not require `Type:`.
