<template>
  <div class="reclassification-page">
    <section class="panel">
      <div class="panel-title">历史重分类</div>
      <a-alert
        class="intro"
        type="info"
        show-icon
        message="保存规则默认只影响之后的新观测，已上传事件的云端分类不会自动变化。只有你在下方明确启动重分类任务后，设备才会用本地仍保留的原始上下文重新解释历史活动，并以更高修订提交结果。"
      />

      <div class="section-title">
        任务进度
        <a-button size="small" class="refresh-button" :loading="refreshingTask" @click="refreshTask">刷新</a-button>
      </div>
      <template v-if="task">
        <div class="task-meta">
          <a-tag :color="task.status === 'open' ? 'blue' : 'default'">
            {{ task.status === "open" ? "进行中" : "已结束" }}
          </a-tag>
          <span>目标规则集版本 <b>v{{ task.target_rule_set_version }}</b></span>
          <span v-if="task.from || task.to">
            时间范围：{{ task.from ? formatInstant(task.from) : "不限" }} ~ {{ task.to ? formatInstant(task.to) : "不限" }}
          </span>
          <span v-else>时间范围：全部历史</span>
          <span>创建于 {{ formatInstant(task.created_at) }}</span>
        </div>
        <div class="count-grid">
          <div class="count-card"><div class="count">{{ task.progress.scanned }}</div><div class="label">设备扫描</div></div>
          <div class="count-card ok"><div class="count">{{ task.progress.reclassified }}</div><div class="label">成功（更高修订）</div></div>
          <div class="count-card"><div class="count">{{ task.progress.unchanged }}</div><div class="label">无需变化</div></div>
          <div class="count-card warn"><div class="count">{{ task.progress.failed }}</div><div class="label">失败</div></div>
          <div class="count-card danger"><div class="count">{{ task.progress.unrecoverable }}</div><div class="label">不可恢复</div></div>
          <div class="count-card"><div class="count">{{ task.progress.devices_reported }}</div><div class="label">已上报设备</div></div>
        </div>
        <a-alert
          v-if="task.progress.unrecoverable > 0"
          class="unrecoverable"
          type="warning"
          show-icon
          message="部分事件不在任何设备的本地保留范围内，原始上下文已不可用，因此无法重分类——它们被明确计入“不可恢复”，绝不会被静默跳过或丢弃。"
        />
        <a-table
          class="device-table"
          :data-source="task.device_reports"
          :columns="reportColumns"
          row-key="device_id"
          :pagination="false"
          size="small"
        />
        <a-button
          v-if="task.status === 'open'"
          class="section-action"
          @click="closeTask"
        >结束任务并审计实际影响</a-button>
      </template>
      <a-empty v-else description="还没有重分类任务。保存或发布规则不会改动历史报表，明确启动任务后才会重分类。" />
    </section>

    <section class="panel">
      <div class="section-title">影响预估（只读，不改动任何数据）</div>
      <a-space class="estimate-toolbar" wrap>
        <a-button :loading="estimating" @click="estimate()">计算预估</a-button>
        <span class="hint">预估范围（可选，UTC）：</span>
        <a-date-picker v-model:value="estimateFrom" show-time placeholder="开始时间" />
        <span>~</span>
        <a-date-picker v-model:value="estimateTo" show-time placeholder="结束时间" />
      </a-space>
      <template v-if="estimateResult">
        <div class="estimate-summary">
          预计涉及 <b>{{ estimateResult.total_events }}</b> 条活动事件，
          来自 <b>{{ estimateResult.devices.length }}</b> 台设备。
        </div>
        <a-table
          :data-source="estimateResult.devices"
          :columns="estimateColumns"
          row-key="device_id"
          :pagination="false"
          size="small"
        />
        <a-alert
          class="limits"
          type="warning"
          show-icon
          message="可重分类限制：只有已定稿、非 AFK、未被人工修正或作废的活动事件在预估范围内；设备只能重算其本地仍保留原始上下文的事件，超出设备保留期的事件将计入“不可恢复”。人工修正永远优先于规则重分类。当前由 Windows 采集器执行重分类（Android 用量观测的原始系统记录由操作系统短期保留，重分类后续票据再评估）。"
        />
      </template>
    </section>

    <section class="panel">
      <div class="section-title">启动重分类任务</div>
      <a-form layout="vertical" class="start-form">
        <a-form-item label="目标规则集版本（设备必须缓存到该版本才会开始重算）">
          <a-select v-model:value="targetVersion" style="max-width: 320px">
            <a-select-option :value="publishedVersion">当前已发布 v{{ publishedVersion }}</a-select-option>
          </a-select>
        </a-form-item>
        <a-form-item label="时间范围（可选，超出范围的历史不参与本次任务）">
          <a-space wrap>
            <a-date-picker v-model:value="taskFrom" show-time placeholder="开始时间" />
            <span>~</span>
            <a-date-picker v-model:value="taskTo" show-time placeholder="结束时间" />
          </a-space>
        </a-form-item>
        <a-popconfirm
          title="启动后设备将重算本地保留的历史活动并提交更高修订，确认启动？"
          ok-text="明确启动"
          cancel-text="取消"
          @confirm="startTask"
        >
          <a-button type="primary" :loading="starting">明确启动重分类任务</a-button>
        </a-popconfirm>
      </a-form>
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import dayjs, { type Dayjs } from "dayjs";
import {
  closeReclassificationTask,
  createReclassificationTask,
  fetchCurrentReclassificationTask,
  fetchReclassificationEstimate,
} from "../api/reclassification";
import { fetchClassificationRuleSet } from "../api/classification";
import type { ReclassificationEstimate, ReclassificationTaskStatus } from "../generated/contract-models";
import { message } from "ant-design-vue";

const estimateResult = ref<ReclassificationEstimate | null>(null);
const estimateFrom = ref<Dayjs | null>(null);
const estimateTo = ref<Dayjs | null>(null);
const estimating = ref(false);

const task = ref<ReclassificationTaskStatus | null>(null);
const refreshingTask = ref(false);
const publishedVersion = ref(1);
const targetVersion = ref(1);
const taskFrom = ref<Dayjs | null>(null);
const taskTo = ref<Dayjs | null>(null);
const starting = ref(false);

const estimateColumns = [
  { title: "设备", dataIndex: "device_id", key: "device_id" },
  { title: "平台", key: "platform", width: 90 },
  { title: "可重分类事件数", dataIndex: "event_count", key: "event_count", width: 130 },
  { title: "最早观测", key: "earliest", width: 180 },
  { title: "最晚观测", key: "latest", width: 180 },
];

const reportColumns = [
  { title: "设备", dataIndex: "device_id", key: "device_id" },
  { title: "平台", key: "platform", width: 90 },
  { title: "扫描", dataIndex: "scanned", key: "scanned", width: 70 },
  { title: "成功", dataIndex: "reclassified", key: "reclassified", width: 70 },
  { title: "无需变化", dataIndex: "unchanged", key: "unchanged", width: 90 },
  { title: "失败", dataIndex: "failed", key: "failed", width: 70 },
  { title: "不可恢复", dataIndex: "unrecoverable", key: "unrecoverable", width: 90 },
  { title: "上报时间", key: "reported_at", width: 180 },
];

onMounted(async () => {
  await Promise.all([refreshTask(), refreshPublishedVersion()]);
});

async function refreshTask(): Promise<void> {
  refreshingTask.value = true;
  try {
    task.value = await fetchCurrentReclassificationTask();
  } finally {
    refreshingTask.value = false;
  }
}

async function refreshPublishedVersion(): Promise<void> {
  try {
    const ruleSet = await fetchClassificationRuleSet();
    publishedVersion.value = ruleSet.rule_set_version;
    targetVersion.value = ruleSet.rule_set_version;
  } catch {
    // 版本仅用于默认值；读取失败时保留上次的值，启动失败会有明确报错。
  }
}

function wire(value: Dayjs | null): string | undefined {
  return value ? value.toISOString() : undefined;
}

async function estimate(): Promise<void> {
  estimating.value = true;
  try {
    estimateResult.value = await fetchReclassificationEstimate({
      from: wire(estimateFrom.value),
      to: wire(estimateTo.value),
    });
  } catch (err) {
    message.error(describe(err, "计算预估失败"));
  } finally {
    estimating.value = false;
  }
}

async function startTask(): Promise<void> {
  starting.value = true;
  try {
    task.value = await createReclassificationTask({
      target_rule_set_version: targetVersion.value,
      from: wire(taskFrom.value),
      to: wire(taskTo.value),
    });
    estimateResult.value = task.value.estimate;
    message.success(`任务已启动，目标规则集版本 v${task.value.target_rule_set_version}，设备将在轮询后开始处理`);
  } catch (err) {
    message.error(describe(err, "启动任务失败"));
  } finally {
    starting.value = false;
  }
}

async function closeTask(): Promise<void> {
  if (!task.value) return;
  try {
    task.value = await closeReclassificationTask(task.value.task_id);
    message.success("任务已结束，实际影响数量已写入审计记录");
  } catch (err) {
    message.error(describe(err, "结束任务失败"));
  }
}

function describe(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

function formatInstant(value: string): string {
  return dayjs(value).format("YYYY-MM-DD HH:mm");
}
</script>

<style scoped>
.reclassification-page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.intro {
  margin-bottom: 12px;
}

.section-title {
  font-weight: 600;
  margin: 8px 0;
  display: flex;
  align-items: center;
  gap: 8px;
}

.refresh-button {
  font-weight: 400;
}

.task-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 12px;
}

.count-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 8px;
  margin-bottom: 12px;
}

.count-card {
  background: rgba(0, 0, 0, 0.03);
  border-radius: 8px;
  padding: 10px 12px;
  text-align: center;
}

.count-card.ok .count { color: #389e0d; }
.count-card.warn .count { color: #d48806; }
.count-card.danger .count { color: #cf1322; }

.count {
  font-size: 22px;
  font-weight: 600;
}

.label {
  font-size: 12px;
  color: rgba(0, 0, 0, 0.45);
}

.unrecoverable {
  margin-bottom: 12px;
}

.device-table {
  margin-bottom: 12px;
}

.section-action {
  margin-top: 4px;
}

.estimate-toolbar {
  margin-bottom: 12px;
}

.hint {
  color: rgba(0, 0, 0, 0.45);
}

.estimate-summary {
  margin: 8px 0;
}

.limits {
  margin-top: 12px;
}

.start-form {
  max-width: 640px;
}
</style>
