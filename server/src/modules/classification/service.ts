import { z } from "zod";
import type {
  ClassificationRule,
  ClassificationRuleKind,
  ClassificationRulePlatform,
  ClassificationRuleSet,
  SemanticEntity,
} from "../../generated/contract-models.js";
import { ClassificationRuleSetModel } from "../../db/models.js";
import { AppError } from "../../shared/errors.js";
import { recordAuditLog } from "../../shared/audit.js";

/**
 * The classification control plane (SPEC implementation decisions 17-18): the
 * Owner publishes one versioned rule set document; devices download it, cache
 * the last successful version, and execute it locally. Only Owner-approved
 * entities exist here — unapproved project names stay on the collecting
 * device and are represented there by device-secret HMAC opaque identifiers.
 */

/** Stable slug identifiers for entities and rules; they are cited by uploads. */
const SLUG_PATTERN = /^[a-z][a-z0-9._-]{0,99}$/;

const semanticEntitySchema = z.strictObject({
  entity_id: z.string().regex(SLUG_PATTERN),
  kind: z.enum(["service", "project"]),
  name: z.string().min(1).max(100),
});

const classificationRuleInputSchema = z.strictObject({
  rule_id: z.string().regex(SLUG_PATTERN),
  platform: z.enum(["windows", "android", "any"]).default("any"),
  kind: z.enum(["application", "title_keyword", "title_regex"]),
  pattern: z.string().min(1).max(300),
  priority: z.number().int().min(-100_000).max(100_000).default(0),
  confidence: z.number().min(0).max(1).optional(),
  subject_entity_id: z.string().min(1).optional(),
  dynamic: z.boolean().optional(),
});

const ruleSetUpdateSchema = z.strictObject({
  entities: z.array(semanticEntitySchema).max(500),
  rules: z.array(classificationRuleInputSchema).max(2000),
});

type ParsedEntity = z.infer<typeof semanticEntitySchema>;
type ParsedRuleInput = z.infer<typeof classificationRuleInputSchema>;

/** Default confidence per rule kind: exact identity is certain, extraction less so. */
const DEFAULT_CONFIDENCE: Record<ClassificationRuleKind, number> = {
  application: 1,
  title_regex: 0.9,
  title_keyword: 0.8,
};

interface StoredRuleSet {
  version: number;
  entities: unknown;
  rules: unknown;
  updated_at: Date;
}

/**
 * Reads the active rule set. Version 0 with empty entities and rules applies
 * until the Owner publishes the first document, so untouched instances serve
 * a valid, cacheable, empty rule set.
 */
export async function readRuleSet(userId: string): Promise<ClassificationRuleSet> {
  const stored = await ClassificationRuleSetModel.findOne({ user_id: userId }).lean<StoredRuleSet | null>();
  if (!stored) {
    return { rule_set_version: 0, updated_at: null, entities: [], rules: [] };
  }
  return {
    rule_set_version: stored.version,
    updated_at: stored.updated_at.toISOString(),
    entities: sortEntities(stored.entities as SemanticEntity[]),
    rules: sortRules(stored.rules as ClassificationRule[]),
  };
}

/**
 * Replaces the rule set document. Every successful update bumps the rule set
 * version; each created or changed rule receives its own incremented version
 * (unchanged rules keep theirs so past classifications stay explainable).
 * The document is validated as a whole — every referenced entity must exist
 * in the same update — and the transition is audited.
 */
export async function updateRuleSet(
  userId: string,
  actorId: string | null,
  input: unknown,
): Promise<ClassificationRuleSet> {
  const parsed = ruleSetUpdateSchema.safeParse(input);
  if (!parsed.success) {
    throw new AppError(400, "The rule set update is missing fields or has invalid values.", "invalid_rule_set");
  }
  validateConsistency(parsed.data.entities, parsed.data.rules);

  const stored = await ClassificationRuleSetModel.findOne({ user_id: userId }).lean<StoredRuleSet | null>();
  const previousRules = (stored?.rules ?? []) as ClassificationRule[];
  const previousVersion = stored?.version ?? 0;
  const now = new Date();

  const { rules: nextRules, added, updated, removed } = mergeRules(previousRules, parsed.data.rules, now);
  const nextVersion = previousVersion + 1;

  await ClassificationRuleSetModel.updateOne(
    { user_id: userId },
    {
      $set: {
        version: nextVersion,
        entities: parsed.data.entities,
        rules: nextRules,
        updated_at: now,
      },
    },
    { upsert: true },
  );

  const previousEntityIds = new Set(((stored?.entities ?? []) as SemanticEntity[]).map((entity) => entity.entity_id));
  await recordAuditLog({
    userId,
    actorId,
    action: "classification_rules.update",
    details: {
      from_version: previousVersion,
      to_version: nextVersion,
      entities_total: parsed.data.entities.length,
      rules_total: nextRules.length,
      rules_added: added,
      rules_updated: updated,
      rules_removed: removed,
      entities_removed: [...previousEntityIds].filter(
        (id) => !parsed.data.entities.some((entity) => entity.entity_id === id),
      ),
    },
  });

  return {
    rule_set_version: nextVersion,
    updated_at: now.toISOString(),
    entities: sortEntities(parsed.data.entities as unknown as SemanticEntity[]),
    rules: sortRules(nextRules),
  };
}

interface NormalizedRule {
  rule_id: string;
  platform: ClassificationRulePlatform;
  kind: ClassificationRuleKind;
  pattern: string;
  priority: number;
  confidence: number;
  subject_entity_id: string | null;
  dynamic: boolean;
}

/**
 * Whole-document consistency: rule targets must resolve inside the same
 * update, dynamic discovery is only expressible as a title regex, and a
 * dynamic pattern must actually capture a candidate project name. Invalid
 * regular expressions are rejected here so devices never receive a rule they
 * cannot execute.
 */
function validateConsistency(entities: ParsedEntity[], rules: ParsedRuleInput[]): void {
  const entityIds = new Set(entities.map((entity) => entity.entity_id));
  if (entityIds.size !== entities.length) {
    throw new AppError(400, "Entity identifiers must be unique within the rule set.", "duplicate_entity");
  }
  const ruleIds = new Set<string>();
  for (const rule of rules) {
    if (ruleIds.has(rule.rule_id)) {
      throw new AppError(400, `Rule identifiers must be unique within the rule set: ${rule.rule_id}.`, "duplicate_rule");
    }
    ruleIds.add(rule.rule_id);
    const hasEntityTarget = rule.subject_entity_id !== undefined;
    const isDynamic = rule.dynamic === true;
    if (hasEntityTarget === isDynamic) {
      throw new AppError(
        400,
        `Rule ${rule.rule_id} must map to exactly one target: an approved entity or dynamic discovery.`,
        "invalid_rule_target",
      );
    }
    if (hasEntityTarget && !entityIds.has(rule.subject_entity_id as string)) {
      throw new AppError(
        400,
        `Rule ${rule.rule_id} references entity ${rule.subject_entity_id}, which is not part of the same update.`,
        "unknown_entity",
      );
    }
    if (isDynamic) {
      if (rule.kind !== "title_regex") {
        throw new AppError(400, `Dynamic discovery rule ${rule.rule_id} must be a title_regex rule.`, "invalid_dynamic_rule");
      }
      if (captureGroupCount(rule.pattern) < 1) {
        throw new AppError(
          400,
          `Dynamic discovery rule ${rule.rule_id} must contain at least one capture group for the project name.`,
          "invalid_dynamic_rule",
        );
      }
    }
    if (rule.kind === "title_regex" && !compiles(rule.pattern)) {
      throw new AppError(400, `Rule ${rule.rule_id} pattern is not a valid regular expression.`, "invalid_pattern");
    }
  }
}

/** Counts capture groups using the empty-alternative trick; 0 when the pattern does not compile. */
function captureGroupCount(pattern: string): number {
  try {
    return (new RegExp(`${pattern}|`).exec("")?.length ?? 1) - 1;
  } catch {
    return 0;
  }
}

function compiles(pattern: string): boolean {
  try {
    new RegExp(pattern);
    return true;
  } catch {
    return false;
  }
}

/**
 * Versioning: a rule whose normalized definition is unchanged keeps its
 * version; a new rule starts at version 1; a changed rule increments its
 * previous version. Deleted rules simply leave the document — past uploads
 * keep citing their rule id and version as explainable provenance.
 */
function mergeRules(
  previousRules: ClassificationRule[],
  submitted: ParsedRuleInput[],
  now: Date,
): { rules: ClassificationRule[]; added: number; updated: number; removed: number } {
  const previousById = new Map(previousRules.map((rule) => [rule.rule_id, rule]));
  const rules: ClassificationRule[] = submitted.map((rule) => {
    const normalized = normalizeRule(rule);
    const existing = previousById.get(rule.rule_id);
    if (existing && sameDefinition(existing, normalized)) {
      return { ...normalized, version: existing.version, updated_at: existing.updated_at };
    }
    return {
      ...normalized,
      version: (existing?.version ?? 0) + 1,
      updated_at: now.toISOString(),
    };
  });
  const added = submitted.filter((rule) => !previousById.has(rule.rule_id)).length;
  // Only existing rules whose definition actually changed count as updated;
  // resubmitting an unchanged rule is a no-op for its version.
  const updated = submitted.filter((rule) => {
    const existing = previousById.get(rule.rule_id);
    return existing !== undefined && !sameDefinition(existing, normalizeRule(rule));
  }).length;
  const removed = previousRules.filter((rule) => !submitted.some((next) => next.rule_id === rule.rule_id)).length;
  return { rules, added, updated, removed };
}

function normalizeRule(rule: ParsedRuleInput): NormalizedRule {
  return {
    rule_id: rule.rule_id,
    platform: rule.platform ?? "any",
    kind: rule.kind,
    pattern: rule.pattern,
    priority: rule.priority ?? 0,
    confidence: rule.confidence ?? DEFAULT_CONFIDENCE[rule.kind],
    subject_entity_id: rule.subject_entity_id ?? null,
    dynamic: rule.dynamic ?? false,
  };
}

/** Definition equality ignores the server-managed version and timestamp. */
function sameDefinition(existing: ClassificationRule, normalized: NormalizedRule): boolean {
  return existing.platform === normalized.platform
    && existing.kind === normalized.kind
    && existing.pattern === normalized.pattern
    && existing.priority === normalized.priority
    && existing.confidence === normalized.confidence
    && (existing.subject_entity_id ?? null) === normalized.subject_entity_id
    && existing.dynamic === normalized.dynamic;
}

function sortEntities(entities: SemanticEntity[]): SemanticEntity[] {
  return [...entities].sort((a, b) => a.entity_id.localeCompare(b.entity_id));
}

/** Rules are distributed in execution order: priority first, rule_id as the deterministic tiebreak. */
function sortRules(rules: ClassificationRule[]): ClassificationRule[] {
  return [...rules].sort((a, b) =>
    b.priority - a.priority || a.rule_id.localeCompare(b.rule_id));
}
