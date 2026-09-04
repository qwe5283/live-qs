<template>
  <a-modal
    :open="event !== null"
    :title="`修正事件 ${shortEventId}`"
    :confirm-loading="submitting"
    ok-text="提交修正"
    cancel-text="取消"
    :width="560"
    @cancel="close"
    @ok="submit"
  >
    <div v-if="event" class="correction-form">
      <a-alert
        type="info"
        show-icon
        class="correction-note"
        message="修正会以同一事件标识提交更高修订（保留原观测与来源），提交后设备重传无法覆盖人工解释。"
      />
      <a-form layout="vertical">
        <a-form-item v-for="field in fields" :key="field.path" :label="field.label">
          <a-switch
            v-if="field.kind === 'boolean'"
            v-model:checked="booleanValues[field.path]"
          />
          <a-select
            v-else-if="field.kind === 'select'"
            v-model:value="textValues[field.path]"
            :options="field.options ?? []"
          />
          <a-input-number
            v-else-if="field.kind === 'number'"
            v-model:value="numberValues[field.path]"
            style="width: 100%"
            :precision="0"
          />
          <a-input
            v-else-if="field.kind === 'money'"
            v-model:value="textValues[field.path]"
            placeholder="精确到分，如 21.50"
            suffix="元"
          />
          <a-input
            v-else-if="field.kind === 'datetime'"
            v-model:value="textValues[field.path]"
            type="datetime-local"
            step="1"
          />
          <a-input v-else v-model:value="textValues[field.path]" />
        </a-form-item>
        <a-form-item label="作废此观测（误报）">
          <a-switch v-model:checked="invalidate" />
          <span class="correction-hint">作废后退出默认时间线与统计，观测保留可审计。</span>
        </a-form-item>
        <a-form-item label="原因（可选，记入审计）">
          <a-textarea v-model:value="reason" :rows="2" :maxlength="500" show-count />
        </a-form-item>
      </a-form>
      <a-typography-text type="secondary" class="correction-hint">
        时间按浏览器本地时区录入，提交为 UTC 瞬时。金额以整数最小单位（分）存储。
      </a-typography-text>
    </div>
  </a-modal>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from "vue";
import { message } from "ant-design-vue";
import {
  correctionFieldsFor,
  minorToYuanText,
  readEventPath,
  submitCorrection,
  yuanTextToMinor,
} from "../../api/corrections";
import type { CorrectableFieldDescriptor } from "../../api/corrections";
import type { EventCorrectionRequest, VersionedEvent } from "../../generated/contract-models";

const props = defineProps<{ event: VersionedEvent | null }>();
const emit = defineEmits<{ corrected: []; closed: [] }>();

const textValues = reactive<Record<string, string>>({});
const numberValues = reactive<Record<string, number | undefined>>({});
const booleanValues = reactive<Record<string, boolean>>({});
const invalidate = ref(false);
const reason = ref("");
const submitting = ref(false);

const fields = computed<CorrectableFieldDescriptor[]>(() => (props.event ? correctionFieldsFor(props.event) : []));
const shortEventId = computed(() => props.event?.event_id.slice(0, 8) ?? "");

watch(() => props.event, (event) => {
  if (!event) return;
  invalidate.value = false;
  reason.value = "";
  for (const field of fields.value) {
    const current = readEventPath(event, field.path);
    if (field.kind === "boolean") {
      booleanValues[field.path] = current === true;
    } else if (field.kind === "number") {
      numberValues[field.path] = typeof current === "number" ? current : undefined;
    } else if (field.kind === "money") {
      textValues[field.path] = typeof current === "number" ? minorToYuanText(current) : "";
    } else if (field.kind === "datetime") {
      textValues[field.path] = toLocalInputValue(typeof current === "string" ? current : null);
    } else {
      textValues[field.path] = typeof current === "string" ? current : "";
    }
  }
});

/** Converts an ISO instant to the browser-local datetime-local input value. */
function toLocalInputValue(iso: string | null): string {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function buildRequest(): EventCorrectionRequest | string {
  const requestFields: Array<{ path: string; value: unknown }> = [];
  for (const field of fields.value) {
    if (field.kind === "boolean") {
      requestFields.push({ path: field.path, value: booleanValues[field.path] === true });
      continue;
    }
    if (field.kind === "number") {
      const value = numberValues[field.path];
      if (value === undefined) return `请填写${field.label}`;
      requestFields.push({ path: field.path, value });
      continue;
    }
    if (field.kind === "money") {
      const minor = yuanTextToMinor(textValues[field.path] ?? "");
      if (minor === null) return `金额需为最多两位小数的正数（${field.label}）`;
      requestFields.push({ path: field.path, value: minor });
      continue;
    }
    if (field.kind === "datetime") {
      const raw = textValues[field.path] ?? "";
      if (!raw) return `请填写${field.label}`;
      const instant = new Date(raw);
      if (Number.isNaN(instant.getTime())) return `时间无效（${field.label}）`;
      requestFields.push({ path: field.path, value: instant.toISOString() });
      continue;
    }
    const text = (textValues[field.path] ?? "").trim();
    if (field.required && !text) return `请填写${field.label}`;
    if (!field.required && !text) continue; // Optional labels stay untouched when blank.
    requestFields.push({ path: field.path, value: text });
  }
  return { fields: requestFields, reason: reason.value.trim() || null, invalidate: invalidate.value };
}

function close() {
  emit("closed");
}

async function submit() {
  if (!props.event) return;
  const body = buildRequest();
  if (typeof body === "string") {
    message.warning(body);
    return;
  }
  submitting.value = true;
  try {
    const result = await submitCorrection(props.event.event_id, body);
    const impactText = result.impact
      .map((entry) => `${entry.metric} ${entry.result_count} 天`)
      .join("、");
    message.success(`修正已提交（修订 ${result.revision}）${impactText ? `，重建影响：${impactText}` : ""}`);
    emit("corrected");
  } catch (err) {
    message.error(err instanceof Error ? err.message : String(err));
  } finally {
    submitting.value = false;
  }
}
</script>

<style scoped>
.correction-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.correction-note {
  margin-bottom: 4px;
}

.correction-hint {
  font-size: 12px;
  color: rgba(0, 0, 0, 0.45);
}
</style>
