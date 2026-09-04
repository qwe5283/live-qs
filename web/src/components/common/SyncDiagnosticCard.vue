<template>
  <a-card class="diagnostic-card" :class="{ attention: needsAttention }" :bordered="false">
    <div class="device-row">
      <div class="device-main">
        <div class="device-name">{{ diagnostic.device_name ?? diagnostic.device_id }}</div>
        <div class="device-app">
          <a-tag v-if="diagnostic.permanent_failure_count > 0" color="red">永久失败 {{ diagnostic.permanent_failure_count }}</a-tag>
          <a-tag v-if="diagnostic.pending_count > 0" color="orange">待上传 {{ diagnostic.pending_count }}</a-tag>
          <a-tag v-if="diagnostic.permanent_failure_count === 0 && diagnostic.pending_count === 0" color="green">队列已清空</a-tag>
          <span class="snapshot-age">快照 {{ ageText }}</span>
        </div>
      </div>
      <a-tag>{{ diagnostic.platform }}</a-tag>
    </div>
    <div class="diagnostic-meta">
      <div>最后采集：{{ diagnostic.collected_at ? formatInstant(diagnostic.collected_at) : "尚无采集" }}</div>
      <div>最后成功上传：{{ diagnostic.last_successful_upload_at ? formatInstant(diagnostic.last_successful_upload_at) : "尚无成功上传" }}</div>
      <div v-if="diagnostic.oldest_pending_at">
        最早待传：{{ formatInstant(diagnostic.oldest_pending_at) }}
      </div>
    </div>
    <div v-if="diagnostic.recent_errors.length" class="error-list">
      <div v-for="(error, index) in diagnostic.recent_errors" :key="index" class="error-row">
        <a-tag class="error-code" color="red">{{ error.code }}</a-tag>
        <span class="error-message">{{ error.message }}</span>
        <span class="error-time">{{ formatInstant(error.occurred_at) }}</span>
      </div>
    </div>
    <div v-else class="no-errors">最近无同步错误</div>
  </a-card>
</template>

<script setup lang="ts">
import { computed } from "vue";
import type { SyncDiagnostic } from "../../generated/contract-models";

const props = defineProps<{ diagnostic: SyncDiagnostic }>();

// Pending uploads or permanent failures mean missing data is waiting or
// stuck: the Owner must be able to tell that apart from zero activity.
const needsAttention = computed(() => props.diagnostic.pending_count > 0 || props.diagnostic.permanent_failure_count > 0);

const ageText = computed(() => {
  const seconds = props.diagnostic.age_seconds;
  if (seconds < 60) return `${seconds} 秒前`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} 分钟前`;
  return `${Math.floor(minutes / 60)} 小时前`;
});

function formatInstant(instant: string): string {
  return new Date(instant).toLocaleString([], { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}
</script>

<style scoped>
.diagnostic-card.attention {
  border-left: 3px solid #faad14;
}
.device-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.device-main {
  flex: 1;
  min-width: 0;
}
.device-name {
  font-weight: 600;
}
.device-app {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-top: 2px;
}
.snapshot-age {
  color: var(--ant-color-text-secondary, #888);
  font-size: 12px;
}
.diagnostic-meta {
  margin-top: 8px;
  display: grid;
  gap: 2px;
  color: var(--ant-color-text-secondary, #666);
  font-size: 12px;
}
.error-list {
  margin-top: 8px;
  display: grid;
  gap: 4px;
}
.error-row {
  display: flex;
  gap: 8px;
  align-items: baseline;
  font-size: 12px;
}
.error-code {
  flex: none;
}
.error-message {
  flex: 1;
  word-break: break-all;
}
.error-time {
  flex: none;
  color: var(--ant-color-text-secondary, #999);
}
.no-errors {
  margin-top: 8px;
  color: var(--ant-color-text-secondary, #999);
  font-size: 12px;
}
</style>
