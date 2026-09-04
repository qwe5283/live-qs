<template>
  <div class="classification-page">
    <section class="panel">
      <div class="panel-title">语义分类</div>
      <a-alert
        class="classification-intro"
        type="info"
        show-icon
        message="设备在本地用这里发布的规则匹配原始窗口标题，只上传主体、类别、规则标识、版本和置信度；原始标题永远留在设备上。未获批准的项目名称以设备密钥 HMAC 的不透明标识（unapproved-…）出现在云端，批准别名后新观测才使用该主体。"
      />
      <div class="classification-toolbar">
        <a-button :loading="loading" @click="refresh">刷新</a-button>
        <a-button type="primary" :disabled="!dirty" :loading="publishing" @click="publish">
          发布规则集（版本 {{ draft.rule_set_version + 1 }}）
        </a-button>
      </div>
      <a-alert
        v-if="dirty"
        class="classification-dirty"
        type="warning"
        show-icon
        message="有未发布的修改：变更只影响之后的新观测，已上传事件不会被改写。"
      />
      <a-alert
        v-if="message"
        class="classification-dirty"
        :type="messageType"
        show-icon
        :message="message"
      />
      <div class="classification-meta">
        当前规则集版本：<a-tag color="blue">v{{ published.rule_set_version }}</a-tag>
        <span v-if="published.updated_at" class="meta-time">发布于 {{ formatDate(published.updated_at) }}</span>
        <span v-else class="meta-time">尚未发布过（设备缓存为空集）</span>
      </div>

      <div class="section-title">语义主体（已批准别名）</div>
      <a-table
        :data-source="draft.entities"
        :columns="entityColumns"
        row-key="entity_id"
        :pagination="false"
        size="small"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'kind'">
            <a-tag :color="record.kind === 'service' ? 'geekblue' : 'green'">
              {{ record.kind === "service" ? "服务" : "项目" }}
            </a-tag>
          </template>
          <template v-else-if="column.key === 'name'">
            {{ record.name }}
            <a-tag class="approved-tag" color="green">已批准</a-tag>
          </template>
          <template v-else-if="column.key === 'usage'">
            <span v-if="rulesUsingEntity(record.entity_id).length">
              {{ rulesUsingEntity(record.entity_id).map((rule) => rule.rule_id).join("、") }}
            </span>
            <a-typography-text v-else type="secondary">无规则引用</a-typography-text>
          </template>
          <template v-else-if="column.key === 'actions'">
            <a-button size="small" type="link" @click="openEditEntity(record)">编辑</a-button>
            <a-popconfirm
              title="删除该主体？引用它的规则会在同一次发布前要求重新指定目标。"
              ok-text="删除"
              cancel-text="取消"
              @confirm="removeEntity(record.entity_id)"
            >
              <a-button size="small" type="link" danger>删除</a-button>
            </a-popconfirm>
          </template>
        </template>
      </a-table>
      <a-button class="section-add" @click="openCreateEntity">新增语义主体</a-button>

      <div class="section-title">分类规则</div>
      <a-table
        :data-source="sortedRules"
        :columns="ruleColumns"
        row-key="rule_id"
        :pagination="false"
        size="small"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'platform'">
            <a-tag v-if="record.platform === 'any'" color="purple">全平台</a-tag>
            <a-tag v-else-if="record.platform === 'windows'" color="blue">Windows</a-tag>
            <a-tag v-else color="green">Android</a-tag>
          </template>
          <template v-else-if="column.key === 'kind'">
            {{ kindLabel(record.kind) }}
          </template>
          <template v-else-if="column.key === 'pattern'">
            <code class="pattern-code">{{ record.pattern }}</code>
          </template>
          <template v-else-if="column.key === 'target'">
            <a-tag v-if="record.dynamic" color="orange">动态发现（不透明标识）</a-tag>
            <span v-else>{{ entityName(record.subject_entity_id) || record.subject_entity_id }}</span>
          </template>
          <template v-else-if="column.key === 'version'">
            v{{ record.version }}
          </template>
          <template v-else-if="column.key === 'actions'">
            <a-button size="small" type="link" @click="openEditRule(record)">编辑</a-button>
            <a-button size="small" type="link" danger @click="removeRule(record.rule_id)">删除</a-button>
          </template>
        </template>
      </a-table>
      <a-button class="section-add" @click="openCreateRule">新增分类规则</a-button>
    </section>

    <a-modal
      v-model:open="entityModalOpen"
      :title="entityEditingId ? '编辑语义主体' : '新增语义主体'"
      ok-text="保存到草稿"
      cancel-text="取消"
      @ok="submitEntity"
    >
      <a-form layout="vertical">
        <a-form-item label="主体标识（entity_id，上传引用的稳定标识）" required>
          <a-input
            v-model:value="entityForm.entity_id"
            :maxlength="100"
            placeholder="例如 svc.bilibili 或 project.liveqs"
            :disabled="entityEditingId !== null"
          />
          <a-typography-text v-if="entityEditingId !== null" type="secondary" class="field-hint">
            标识创建后不可更改，历史数据靠它保持可比；显示名称随时可改。
          </a-typography-text>
        </a-form-item>
        <a-form-item label="类型" required>
          <a-segmented
            v-model:value="entityForm.kind"
            :options="[
              { label: '服务（跨平台同一服务）', value: 'service' },
              { label: '项目（批准的项目别名）', value: 'project' },
            ]"
          />
        </a-form-item>
        <a-form-item label="批准别名（显示名称）" required>
          <a-input v-model:value="entityForm.name" :maxlength="100" placeholder="例如 哔哩哔哩 / LiveQs" />
          <a-typography-text type="secondary" class="field-hint">
            只有这里批准的名称会存在于服务端；设备发现的未批准项目名称以不透明标识表示。
          </a-typography-text>
        </a-form-item>
      </a-form>
    </a-modal>

    <a-modal
      v-model:open="ruleModalOpen"
      :title="ruleEditingId ? '编辑分类规则' : '新增分类规则'"
      ok-text="保存到草稿"
      cancel-text="取消"
      @ok="submitRule"
    >
      <a-form layout="vertical">
        <a-form-item label="规则标识（rule_id，上传结果引用的稳定标识）" required>
          <a-input
            v-model:value="ruleForm.rule_id"
            :maxlength="100"
            placeholder="例如 edge.bilibili.title"
            :disabled="ruleEditingId !== null"
          />
        </a-form-item>
        <a-form-item label="匹配方式" required>
          <a-select v-model:value="ruleForm.kind">
            <a-select-option value="application">应用 / 包名（精确匹配）</a-select-option>
            <a-select-option value="title_keyword">标题关键词（包含匹配）</a-select-option>
            <a-select-option value="title_regex">标题正则（可提取项目名）</a-select-option>
          </a-select>
        </a-form-item>
        <a-form-item :label="patternLabel" required>
          <a-textarea
            v-model:value="ruleForm.pattern"
            :rows="2"
            :placeholder="patternPlaceholder"
          />
          <a-typography-text v-if="ruleForm.kind === 'title_regex'" type="secondary" class="field-hint">
            正则在设备本地执行；跨平台正则方言存在差异，请保持常用语法。
          </a-typography-text>
        </a-form-item>
        <a-form-item label="适用平台">
          <a-segmented
            v-model:value="ruleForm.platform"
            :options="[
              { label: '全平台', value: 'any' },
              { label: 'Windows', value: 'windows' },
              { label: 'Android', value: 'android' },
            ]"
          />
          <a-typography-text v-if="ruleForm.kind !== 'application' && ruleForm.platform === 'android'" type="secondary" class="field-hint">
            标题规则只在采集窗口标题的平台上生效；Android 用量观测使用应用 / 包名规则。
          </a-typography-text>
        </a-form-item>
        <a-form-item label="分类目标" required>
          <a-radio-group v-model:value="ruleForm.target" :disabled="ruleForm.kind !== 'title_regex' && ruleForm.target === 'dynamic'">
            <a-radio value="entity">指定语义主体</a-radio>
            <a-radio value="dynamic" :disabled="ruleForm.kind !== 'title_regex'">动态发现项目</a-radio>
          </a-radio-group>
        </a-form-item>
        <a-form-item v-if="ruleForm.target === 'entity'" label="主体" required>
          <a-select v-model:value="ruleForm.subject_entity_id" placeholder="选择已批准的主体">
            <a-select-option v-for="entity in draft.entities" :key="entity.entity_id" :value="entity.entity_id">
              {{ entity.name }}（{{ entity.entity_id }}）
            </a-select-option>
          </a-select>
        </a-form-item>
        <a-form-item v-else>
          <a-alert
            type="warning"
            show-icon
            message="动态发现：正则的第 1 个捕获组是候选项目名，设备以设备密钥 HMAC 的不透明标识上传，原始名称不上传；批准别名后为其新建主体并添加指定主体的规则。"
          />
        </a-form-item>
        <a-form-item label="优先级（冲突时数值大者胜，相同则按规则标识排序）">
          <a-input-number v-model:value="ruleForm.priority" :min="-100000" :max="100000" />
        </a-form-item>
        <a-form-item label="置信度（留空按匹配方式默认值）">
          <a-input-number v-model:value="ruleForm.confidence" :min="0" :max="1" :step="0.05" placeholder="自动" />
        </a-form-item>
      </a-form>
    </a-modal>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import dayjs from "dayjs";
import {
  fetchClassificationRuleSet,
  updateClassificationRuleSet,
} from "../api/classification";
import type {
  ClassificationRule,
  ClassificationRuleInput,
  ClassificationRuleKind,
  ClassificationRulePlatform,
  ClassificationRuleSet,
  SemanticEntity,
  SemanticEntityKind,
} from "../generated/contract-models";

const loading = ref(false);
const publishing = ref(false);
const published = ref<ClassificationRuleSet>({ rule_set_version: 0, updated_at: null, entities: [], rules: [] });
const draft = ref<ClassificationRuleSet>({ rule_set_version: 0, updated_at: null, entities: [], rules: [] });
const message = ref("");
const messageType = ref<"success" | "error" | "info">("info");

const entityColumns = [
  { title: "主体标识", dataIndex: "entity_id", key: "entity_id" },
  { title: "类型", key: "kind", width: 90 },
  { title: "批准别名", key: "name" },
  { title: "被规则引用", key: "usage" },
  { title: "操作", key: "actions", width: 130 },
];

const ruleColumns = [
  { title: "规则标识", dataIndex: "rule_id", key: "rule_id" },
  { title: "平台", key: "platform", width: 90 },
  { title: "匹配方式", key: "kind", width: 130 },
  { title: "模式", key: "pattern" },
  { title: "分类目标", key: "target" },
  { title: "优先级", dataIndex: "priority", key: "priority", width: 80 },
  { title: "置信度", dataIndex: "confidence", key: "confidence", width: 80 },
  { title: "版本", key: "version", width: 70 },
  { title: "操作", key: "actions", width: 130 },
];

const dirty = computed(() => JSON.stringify(draft.value) !== JSON.stringify(published.value));

const sortedRules = computed(() =>
  [...draft.value.rules].sort((a, b) => b.priority - a.priority || a.rule_id.localeCompare(b.rule_id)),
);

onMounted(refresh);

async function refresh(): Promise<void> {
  loading.value = true;
  try {
    const ruleSet = await fetchClassificationRuleSet();
    published.value = ruleSet;
    draft.value = clone(ruleSet);
    showMessage("", "info");
  } catch (err) {
    show(err, "加载分类规则集失败");
  } finally {
    loading.value = false;
  }
}

async function publish(): Promise<void> {
  publishing.value = true;
  try {
    const request = {
      entities: draft.value.entities,
      rules: draft.value.rules.map(toRuleInput),
    };
    const saved = await updateClassificationRuleSet(request);
    published.value = saved;
    draft.value = clone(saved);
    showMessage(`规则集已发布为 v${saved.rule_set_version}，设备将在下次刷新时拉取`, "success");
  } catch (err) {
    show(err, "发布规则集失败");
  } finally {
    publishing.value = false;
  }
}

function toRuleInput(rule: ClassificationRule): ClassificationRuleInput {
  return rule.dynamic
    ? { rule_id: rule.rule_id, platform: rule.platform, kind: rule.kind, pattern: rule.pattern, priority: rule.priority, confidence: rule.confidence, dynamic: true }
    : { rule_id: rule.rule_id, platform: rule.platform, kind: rule.kind, pattern: rule.pattern, priority: rule.priority, confidence: rule.confidence, subject_entity_id: rule.subject_entity_id ?? undefined };
}

// --- Entities ---

const entityModalOpen = ref(false);
const entityEditingId = ref<string | null>(null);
const entityForm = reactive<{ entity_id: string; kind: SemanticEntityKind; name: string }>({
  entity_id: "",
  kind: "service",
  name: "",
});

function openCreateEntity(): void {
  entityEditingId.value = null;
  entityForm.entity_id = "";
  entityForm.kind = "service";
  entityForm.name = "";
  entityModalOpen.value = true;
}

function openEditEntity(entity: SemanticEntity): void {
  entityEditingId.value = entity.entity_id;
  entityForm.entity_id = entity.entity_id;
  entityForm.kind = entity.kind;
  entityForm.name = entity.name;
  entityModalOpen.value = true;
}

function submitEntity(): void {
  const entityId = entityForm.entity_id.trim();
  if (!/^[a-z][a-z0-9._-]*$/.test(entityId)) {
    showMessage("主体标识必须以小写字母开头，只能包含小写字母、数字、点、下划线和连字符", "error");
    return;
  }
  if (!entityForm.name.trim()) {
    showMessage("请填写批准别名", "error");
    return;
  }
  const duplicate = draft.value.entities.find((entity) => entity.entity_id === entityId);
  if (duplicate && entityEditingId.value === null) {
    showMessage(`主体标识 ${entityId} 已存在`, "error");
    return;
  }
  if (duplicate && entityEditingId.value !== null) {
    duplicate.kind = entityForm.kind;
    duplicate.name = entityForm.name.trim();
  } else {
    draft.value.entities.push({ entity_id: entityId, kind: entityForm.kind, name: entityForm.name.trim() });
  }
  entityModalOpen.value = false;
}

function removeEntity(entityId: string): void {
  draft.value.entities = draft.value.entities.filter((entity) => entity.entity_id !== entityId);
}

function rulesUsingEntity(entityId: string): ClassificationRule[] {
  return draft.value.rules.filter((rule) => !rule.dynamic && rule.subject_entity_id === entityId);
}

function entityName(entityId: string | null | undefined): string {
  return draft.value.entities.find((entity) => entity.entity_id === entityId)?.name ?? "";
}

// --- Rules ---

const ruleModalOpen = ref(false);
const ruleEditingId = ref<string | null>(null);
const ruleForm = reactive<{
  rule_id: string;
  kind: ClassificationRuleKind;
  pattern: string;
  platform: ClassificationRulePlatform;
  target: "entity" | "dynamic";
  subject_entity_id: string;
  priority: number;
  confidence: number | null;
}>({
  rule_id: "",
  kind: "application",
  pattern: "",
  platform: "any",
  target: "entity",
  subject_entity_id: "",
  priority: 0,
  confidence: null,
});

const patternLabel = computed(() =>
  ruleForm.kind === "application" ? "应用名 / 包名" : ruleForm.kind === "title_keyword" ? "标题关键词" : "标题正则表达式",
);

const patternPlaceholder = computed(() =>
  ruleForm.kind === "application"
    ? "例如 msedge.exe 或 tv.danmaku.bili"
    : ruleForm.kind === "title_keyword"
      ? "例如 bilibili"
      : "例如 ^RIDER-(.+)$",
);

function openCreateRule(): void {
  ruleEditingId.value = null;
  ruleForm.rule_id = "";
  ruleForm.kind = "application";
  ruleForm.pattern = "";
  ruleForm.platform = "any";
  ruleForm.target = "entity";
  ruleForm.subject_entity_id = draft.value.entities[0]?.entity_id ?? "";
  ruleForm.priority = 0;
  ruleForm.confidence = null;
  ruleModalOpen.value = true;
}

function openEditRule(rule: ClassificationRule): void {
  ruleEditingId.value = rule.rule_id;
  ruleForm.rule_id = rule.rule_id;
  ruleForm.kind = rule.kind;
  ruleForm.pattern = rule.pattern;
  ruleForm.platform = rule.platform;
  ruleForm.target = rule.dynamic ? "dynamic" : "entity";
  ruleForm.subject_entity_id = rule.subject_entity_id ?? "";
  ruleForm.priority = rule.priority;
  ruleForm.confidence = rule.confidence;
  ruleModalOpen.value = true;
}

function submitRule(): void {
  const ruleId = ruleForm.rule_id.trim();
  if (!/^[a-z][a-z0-9._-]*$/.test(ruleId)) {
    showMessage("规则标识必须以小写字母开头，只能包含小写字母、数字、点、下划线和连字符", "error");
    return;
  }
  if (!ruleForm.pattern.trim()) {
    showMessage("请填写匹配模式", "error");
    return;
  }
  if (ruleForm.target === "entity" && !ruleForm.subject_entity_id) {
    showMessage("请选择分类目标主体", "error");
    return;
  }
  if (ruleForm.target === "dynamic" && ruleForm.kind !== "title_regex") {
    showMessage("动态发现只能是标题正则规则", "error");
    return;
  }
  const existing = draft.value.rules.find((rule) => rule.rule_id === ruleId);
  const next: ClassificationRule = {
    rule_id: ruleId,
    kind: ruleForm.kind,
    pattern: ruleForm.pattern.trim(),
    platform: ruleForm.platform,
    priority: ruleForm.priority,
    confidence: ruleForm.confidence ?? defaultConfidence(ruleForm.kind),
    subject_entity_id: ruleForm.target === "entity" ? ruleForm.subject_entity_id : null,
    dynamic: ruleForm.target === "dynamic",
    // 版本与发布时间由服务端管理；草稿里沿用旧值展示，发布后刷新。
    version: existing?.version ?? 1,
    updated_at: existing?.updated_at ?? null,
  };
  if (existing) {
    Object.assign(existing, next);
  } else {
    draft.value.rules.push(next);
  }
  ruleModalOpen.value = false;
}

function removeRule(ruleId: string): void {
  draft.value.rules = draft.value.rules.filter((rule) => rule.rule_id !== ruleId);
}

function defaultConfidence(kind: ClassificationRuleKind): number {
  return kind === "application" ? 1 : kind === "title_regex" ? 0.9 : 0.8;
}

function kindLabel(kind: ClassificationRuleKind): string {
  return kind === "application" ? "应用 / 包名" : kind === "title_keyword" ? "标题关键词" : "标题正则";
}

function formatDate(value: string): string {
  return dayjs(value).format("YYYY-MM-DD HH:mm");
}

function clone(ruleSet: ClassificationRuleSet): ClassificationRuleSet {
  return JSON.parse(JSON.stringify(ruleSet)) as ClassificationRuleSet;
}

function show(err: unknown, fallback: string): void {
  message.value = err instanceof Error ? err.message : fallback;
  messageType.value = "error";
}

function showMessage(text: string, type: "success" | "error" | "info"): void {
  message.value = text;
  messageType.value = type;
}
</script>

<style scoped>
.classification-page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.classification-intro {
  margin-bottom: 12px;
}

.classification-toolbar {
  display: flex;
  gap: 8px;
  margin-bottom: 12px;
}

.classification-dirty {
  margin-bottom: 12px;
}

.classification-meta {
  margin-bottom: 8px;
  display: flex;
  align-items: center;
  gap: 8px;
}

.meta-time {
  font-size: 12px;
  color: rgba(0, 0, 0, 0.45);
}

.section-title {
  font-weight: 600;
  margin: 16px 0 8px;
}

.section-add {
  margin-top: 8px;
}

.approved-tag {
  margin-left: 6px;
}

.pattern-code {
  word-break: break-all;
}

.field-hint {
  display: block;
  font-size: 12px;
  margin-top: 4px;
}
</style>
