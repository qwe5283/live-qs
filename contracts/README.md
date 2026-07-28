# LiveQs Contracts

This directory is the language-neutral V1 protocol authority. `openapi.yaml`
defines HTTP behavior; JSON Schema under `schemas/` defines persisted event
shapes and semantics. Runtime DTOs must not become an independent contract.

## Commands

Install this component independently from the repository root:

```powershell
npm --prefix contracts ci
```

- `npm --prefix contracts test` validates OpenAPI and all valid/invalid examples.
- `npm --prefix contracts run generate` refreshes derived TypeScript, C#, and Kotlin models.
- `npm --prefix contracts run check:generated` fails if committed models have drifted.
- `.\scripts\check.ps1 contracts` runs the complete contract gate.

Generated models live in each active component's `generated/` directory. Do not
edit them manually. Component-specific mapping or runtime validation may wrap
these types but may not redefine their wire shape.

## Adding an Event Version

Add one self-contained file named
`schemas/events/<event-type>.v<version>.schema.json`. Set literal `event_type`
and `schema_version` properties, reference the stable envelope, and add at least
one sanitized valid example plus three meaningful invalid examples under
`examples/`. Then regenerate models and run the contract gate.

## Compatibility Rules

Within an existing event schema version, only a new optional field with unchanged
semantics is compatible. The following changes require a new schema version:

- removing or renaming a field;
- making an optional field required;
- changing a type, unit, timestamp boundary, privacy meaning, or source meaning;
- narrowing accepted enum values or constraints;
- changing how historical values must be interpreted.

Adding a new event type is additive. Removing an HTTP operation, changing its
authentication or required scope, changing a response meaning, or making an HTTP
field required is a breaking API change and requires a new API version. Update
OpenAPI before integrations and keep old event schemas so stored history remains
interpretable.
