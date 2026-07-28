export function buildProtocolModelSchema(openApi) {
  const componentSchemas = rewriteOpenApiReferences(openApi.components.schemas);
  const modelNames = Object.keys(componentSchemas).sort();

  return {
    $schema: "https://json-schema.org/draft/2020-12/schema",
    $id: "https://live-qs.local/contracts/generated/protocol.schema.json",
    title: "LiveQs Protocol Models",
    type: "object",
    additionalProperties: false,
    required: modelNames,
    properties: Object.fromEntries(modelNames.map((name) => [name, { $ref: `#/$defs/${name}` }])),
    $defs: componentSchemas,
  };
}

function rewriteOpenApiReferences(value) {
  if (Array.isArray(value)) {
    return value.map(rewriteOpenApiReferences);
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([key, entry]) => {
      if (key !== "$ref") {
        return [key, rewriteOpenApiReferences(entry)];
      }
      if (entry === "./schemas/event.schema.json") {
        return [key, "https://live-qs.local/contracts/schemas/event.schema.json"];
      }
      return [key, entry.replace("#/components/schemas/", "#/$defs/")];
    }));
  }
  return value;
}
