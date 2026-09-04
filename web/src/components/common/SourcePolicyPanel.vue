<template>
  <section v-if="context" class="policy-panel">
    <a-space wrap :size="8">
      <a-tag v-if="context.source_policy_version !== undefined" color="blue">来源策略 v{{ context.source_policy_version }}</a-tag>
      <a-tag v-if="context.data_state" :color="stateColor">{{ stateLabel }}</a-tag>
      <a-tag v-if="(context.pending_confirmation_count ?? 0) > 0" color="orange">待确认 {{ context.pending_confirmation_count }} 笔</a-tag>
      <a-tag v-if="conflicts.length > 0" color="red">来源冲突 {{ conflicts.length }} 处</a-tag>
      <span v-if="context.provenance.length" class="policy-provenance">来源种类：{{ context.provenance.join("、") }}</span>
    </a-space>
    <a-table
      v-if="conflicts.length > 0"
      class="conflict-table"
      size="small"
      :pagination="false"
      :columns="conflictColumns"
      :data-source="conflictRows"
      row-key="key"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'events'">
          <a-tooltip :title="record.eventIds">
            <span>{{ record.events }}</span>
          </a-tooltip>
        </template>
        <template v-else>{{ record[column.key] }}</template>
      </template>
    </a-table>
    <a-typography-text v-if="conflicts.length > 0" type="secondary" class="policy-note">
      竞争观测全部保留且可单独查阅，仅按版本化策略不计入规范化结果；系统从不按相似度删除观测。
    </a-typography-text>
  </section>
</template>

<script setup lang="ts">
import { computed } from "vue";
import type { QueryContext, SourceConflict } from "../../generated/contract-models";

const props = defineProps<{
  context: QueryContext | null;
}>();

const conflicts = computed<SourceConflict[]>(() => props.context?.source_conflicts ?? []);

const stateLabel = computed(() => {
  if (props.context?.data_state === "zero") return "显式零值（有观测，合计为 0）";
  if (props.context?.data_state === "no_data") return "无数据（缺失 ≠ 零）";
  return "有观测";
});

const stateColor = computed(() => {
  if (props.context?.data_state === "zero") return "gold";
  if (props.context?.data_state === "no_data") return "default";
  return "green";
});

const conflictColumns = [
  { title: "指标", dataIndex: "metric" },
  { title: "策略选中", dataIndex: "selected" },
  { title: "竞争来源（保留）", dataIndex: "competing" },
  { title: "事件（选中/保留）", dataIndex: "events", key: "events" },
  { title: "冲突时间（UTC）", dataIndex: "range" },
];

const conflictRows = computed(() => conflicts.value.map((conflict, index) => ({
  key: `${conflict.metric}-${index}`,
  metric: conflict.metric,
  selected: `${conflict.selected_source}（策略 v${conflict.policy_version}）`,
  competing: conflict.competing_sources.join("、"),
  events: `选中 ${conflict.selected_event_ids.length} 条 · 保留 ${conflict.competing_event_ids.length} 条`,
  eventIds: [
    ...conflict.selected_event_ids.map((id) => `选中 ${id}`),
    ...conflict.competing_event_ids.map((id) => `保留 ${id}`),
  ].join("\n"),
  range: `${formatInstant(conflict.from)} – ${formatInstant(conflict.to)}`,
})));

function formatInstant(value: string): string {
  return value.replace("T", " ").replace(/\.000Z$/, "Z");
}
</script>

<style scoped>
.policy-panel {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.policy-provenance {
  color: rgba(0, 0, 0, 0.45);
  font-size: 12px;
}

.policy-note {
  font-size: 12px;
}

.conflict-table {
  max-width: 980px;
}
</style>
