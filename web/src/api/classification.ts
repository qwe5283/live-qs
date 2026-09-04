import { apiGet, apiPut } from "./client";
import type { ClassificationRuleSet, ClassificationRuleSetUpdateRequest } from "../generated/contract-models";

/**
 * The versioned classification control plane: the Owner manages semantic
 * entities and rules as one document; devices read the same document with a
 * rules:read Device Token and execute it locally.
 */
export function fetchClassificationRuleSet(): Promise<ClassificationRuleSet> {
  return apiGet<ClassificationRuleSet>("/api/v1/classification/ruleset");
}

export function updateClassificationRuleSet(request: ClassificationRuleSetUpdateRequest): Promise<ClassificationRuleSet> {
  return apiPut<ClassificationRuleSet>("/api/v1/classification/ruleset", request);
}
