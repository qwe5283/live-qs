<template>
  <div class="page-stack">
    <a-alert v-if="error" type="error" show-icon :message="error" />

    <section class="toolbar-panel">
      <a-space wrap>
        <a-date-picker v-model:value="startDate" />
        <a-date-picker v-model:value="endDate" />
        <a-button type="primary" :loading="loading" @click="load">查询</a-button>
        <a-tag color="blue">报表时区：{{ reportTimezone }}</a-tag>
      </a-space>
    </section>

    <a-alert
      v-if="completeness === 'partial'"
      type="warning"
      show-icon
      message="查询范围内存在被凭据隐私上限或事件类型限制隐藏的健康数据，当前结果不完整（partial）。"
    />

    <SourcePolicyPanel :context="context" />

    <div class="metric-grid">
      <MetricCard
        title="步数"
        :value="stepsAvailable ? steps.toLocaleString() : '无数据'"
        :note="stepsNote"
        :icon="RiseOutlined"
      />
      <MetricCard
        title="睡眠（来源提供的区间）"
        :value="sleepAvailable ? formatMinutes(sleepMinutesInRange) : '无数据'"
        :note="sleepNote"
        :icon="ClockCircleOutlined"
      />
      <MetricCard
        title="平均心率"
        :value="heartRateAvailable ? `${Math.round(heartRateAverage)} bpm` : '无数据'"
        :note="heartRateAvailable ? `${heartRateCount} 次采样` : '范围内没有心率采样'"
        :icon="HeartOutlined"
      />
      <MetricCard
        title="数据来源（origin）"
        :value="String(originRows.length)"
        :note="`共 ${events.length} 条观测`"
        :icon="DatabaseOutlined"
      />
    </div>

    <section class="panel">
      <div class="panel-title">按日覆盖（缺失 ≠ 零）</div>
      <a-table
        :data-source="coverageRows"
        :columns="coverageColumns"
        :pagination="false"
        size="small"
        row-key="date"
      >
        <template #bodyCell="{ column, text }">
          <template v-if="column.key === 'steps' || column.key === 'heartRate' || column.key === 'sleep'">
            <a-tag v-if="text > 0" color="green">{{ text }} 条</a-tag>
            <a-tag v-else color="default">无数据</a-tag>
          </template>
          <template v-else>{{ text }}</template>
        </template>
      </a-table>
      <a-typography-text type="secondary" class="coverage-note">
        覆盖按观测开始时刻归属到报表时区日。「无数据」表示该日没有收到相应来源的观测，而不是零值。
      </a-typography-text>
    </section>

    <section class="panel">
      <div class="panel-title">按来源（Health Connect data origin）</div>
      <a-table
        :data-source="originRows"
        :columns="originColumns"
        :pagination="false"
        size="small"
        row-key="origin"
      />
    </section>

    <div class="dashboard-grid">
      <section class="panel">
        <div class="panel-title">步数趋势（仅含存在观测的日期）</div>
        <BaseChart v-if="stepsOption" :option="stepsOption" />
        <EmptyState v-else description="暂无步数观测" />
      </section>
      <section class="panel">
        <div class="panel-title">心率采样</div>
        <BaseChart v-if="heartRateOption" :option="heartRateOption" />
        <EmptyState v-else description="暂无心率采样" />
      </section>
    </div>

    <section class="panel">
      <div class="panel-title">睡眠区间（仅来源提供；系统从不把设备空闲推断为睡眠）</div>
      <a-table
        :data-source="sleepRows"
        :columns="sleepColumns"
        :pagination="false"
        size="small"
        row-key="key"
      />
    </section>

    <section class="panel">
      <div class="panel-title">健康观测时间线</div>
      <a-table
        :data-source="timelineRows"
        :columns="timelineColumns"
        :pagination="false"
        size="small"
        row-key="key"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'eventId'">
            <a-tooltip :title="record.fullEventId">
              <span>{{ record.eventId }}</span>
            </a-tooltip>
          </template>
          <template v-else>{{ record[column.key] }}</template>
        </template>
      </a-table>
      <a-button
        v-if="nextCursor"
        class="load-more"
        :loading="loading"
        @click="load(nextCursor)"
      >
        加载更多
      </a-button>
      <a-typography-text v-if="provenanceText" type="secondary">
        来源种类：{{ provenanceText }}
      </a-typography-text>
    </section>
  </div>
</template>

<script setup lang="ts">
import type { EChartsOption } from "echarts";
import { computed, ref } from "vue";
import { ClockCircleOutlined, DatabaseOutlined, HeartOutlined, RiseOutlined } from "@ant-design/icons-vue";
import dayjs, { type Dayjs } from "dayjs";
import { fetchHealthEvents } from "../api/health";
import { fetchOwnerSettings } from "../api/settings";
import type { QueryContext, VersionedEvent } from "../generated/contract-models";
import BaseChart from "../components/charts/BaseChart.vue";
import EmptyState from "../components/common/EmptyState.vue";
import MetricCard from "../components/common/MetricCard.vue";
import SourcePolicyPanel from "../components/common/SourcePolicyPanel.vue";
import {
  addDaysIso,
  dateInTimezone,
  formatCaptureZone,
  formatMinutes,
  formatUtcText,
  todayInTimezone,
  zonedDayRangeUtc,
} from "../utils/date";

const reportTimezone = ref("UTC");
const startDate = ref<Dayjs | null>(null);
const endDate = ref<Dayjs | null>(null);
const events = ref<VersionedEvent[]>([]);
const context = ref<QueryContext | null>(null);
const nextCursor = ref<string | null>(null);
const loading = ref(false);
const error = ref("");

const completeness = computed(() => context.value?.completeness ?? "");
const provenance = computed(() => context.value?.provenance ?? []);

const rangeText = computed(() => ({
  start: startDate.value ? startDate.value.format("YYYY-MM-DD") : "",
  end: endDate.value ? endDate.value.format("YYYY-MM-DD") : "",
}));

const stepSamples = computed(() => events.value.filter((event) => isStep(event) && !withheldIds.value.has(event.event_id)));
const heartRateSamples = computed(() => events.value.filter(isHeartRate));
const sleepSessions = computed(() => events.value.filter((event) => isSleep(event) && !withheldIds.value.has(event.event_id)));

/** Observations the source policy withheld from normalized totals; they stay retained and listed. */
const withheldIds = computed(() => new Set(
  (context.value?.source_conflicts ?? []).flatMap((conflict) => conflict.competing_event_ids),
));
const withheldCount = computed(() => withheldIds.value.size);

const steps = computed(() => stepSamples.value.reduce((total, event) => total + (event.payload.count?.value ?? 0), 0));
const stepsAvailable = computed(() => stepSamples.value.length > 0);
const heartRateCount = computed(() => heartRateSamples.value.length);
const heartRateAverage = computed(() => {
  const samples = heartRateSamples.value;
  return samples.length > 0
    ? samples.reduce((total, event) => total + (event.payload.beats_per_minute ?? 0), 0) / samples.length
    : 0;
});
const heartRateAvailable = computed(() => heartRateSamples.value.length > 0);
const sleepMinutesInRange = computed(() => {
  const range = currentRangeUtc();
  if (!range) return 0;
  const startMs = range.start.getTime();
  const endMs = range.end.getTime();
  let totalMs = 0;
  for (const event of sleepSessions.value) {
    const sessionStart = Date.parse(event.start_at);
    const sessionEnd = event.end_at ? Date.parse(event.end_at) : sessionStart;
    const overlapStart = Math.max(sessionStart, startMs);
    const overlapEnd = Math.min(sessionEnd, endMs);
    totalMs += Math.max(0, overlapEnd - overlapStart);
  }
  return totalMs / 60_000;
});
const sleepAvailable = computed(() => sleepSessions.value.length > 0);

const stepsNote = computed(() => {
  if (!stepsAvailable.value) return "范围内没有步数观测；缺失不会显示为 0";
  if (withheldCount.value > 0) return `已按来源策略排除 ${withheldCount.value} 条竞争观测（保留未计入）`;
  return undefined;
});
const sleepNote = computed(() => {
  if (!sleepAvailable.value) return "范围内没有来源睡眠区间；系统从不推断睡眠";
  if (withheldCount.value > 0) return `已按来源策略排除 ${withheldCount.value} 条竞争观测（保留未计入）`;
  return undefined;
});

function isStep(event: VersionedEvent): boolean {
  return event.event_type === "health.step.sample";
}
function isHeartRate(event: VersionedEvent): boolean {
  return event.event_type === "health.heartrate.sample";
}
function isSleep(event: VersionedEvent): boolean {
  return event.event_type === "health.sleep.session";
}

/** [rangeStart, rangeEnd) instants of the selected local days in the report timezone. */
function currentRangeUtc(): { start: Date; end: Date } | null {
  const start = rangeText.value.start;
  const endExclusive = rangeText.value.end ? addDaysIso(rangeText.value.end, 1) : "";
  if (!start || !endExclusive) return null;
  const startRange = zonedDayRangeUtc(start, reportTimezone.value);
  const endRange = zonedDayRangeUtc(endExclusive, reportTimezone.value);
  return { start: startRange.start, end: endRange.end };
}

/** Per-local-day observation counts; a day without observations shows 无数据, never zero. */
const coverageRows = computed(() => {
  if (!rangeText.value.start || !rangeText.value.end) return [];
  const rows: Array<{ date: string; steps: number; heartRate: number; sleep: number }> = [];
  let day = rangeText.value.start;
  while (day <= rangeText.value.end) {
    rows.push({ date: day, steps: 0, heartRate: 0, sleep: 0 });
    day = addDaysIso(day, 1);
  }
  const index = new Map(rows.map((row) => [row.date, row]));
  for (const event of events.value) {
    const bucket = index.get(dateInTimezone(event.start_at, reportTimezone.value));
    if (!bucket) continue;
    if (isStep(event)) bucket.steps += 1;
    else if (isHeartRate(event)) bucket.heartRate += 1;
    else if (isSleep(event)) bucket.sleep += 1;
  }
  return rows;
});

const coverageColumns = [
  { title: "日期", dataIndex: "date" },
  { title: "步数", dataIndex: "steps", key: "steps" },
  { title: "心率", dataIndex: "heartRate", key: "heartRate" },
  { title: "睡眠", dataIndex: "sleep", key: "sleep" },
];

/** Per-origin attribution: each Health Connect writing application stays distinct. */
const originRows = computed(() => {
  const groups = new Map<string, { origin: string; steps: number; heartRateCount: number; heartRateSum: number; sleepMinutes: number; records: number }>();
  for (const event of events.value) {
    const origin = event.payload.data_origin;
    if (!origin) continue;
    const row = groups.get(origin) ?? { origin, steps: 0, heartRateCount: 0, heartRateSum: 0, sleepMinutes: 0, records: 0 };
    row.records += 1;
    if (isStep(event)) row.steps += event.payload.count?.value ?? 0;
    if (isHeartRate(event)) {
      row.heartRateCount += 1;
      row.heartRateSum += event.payload.beats_per_minute ?? 0;
    }
    if (isSleep(event)) {
      const start = Date.parse(event.start_at);
      const end = event.end_at ? Date.parse(event.end_at) : start;
      row.sleepMinutes += Math.max(0, end - start) / 60_000;
    }
    groups.set(origin, row);
  }
  return [...groups.values()].map((row) => ({
    origin: row.origin,
    records: row.records,
    steps: row.steps > 0 ? row.steps.toLocaleString() : "无数据",
    heartRate: row.heartRateCount > 0 ? `${Math.round(row.heartRateSum / row.heartRateCount)} bpm（${row.heartRateCount} 次）` : "无数据",
    sleep: row.sleepMinutes > 0 ? formatMinutes(row.sleepMinutes) : "无数据",
  }));
});

const originColumns = [
  { title: "来源应用（data_origin）", dataIndex: "origin" },
  { title: "观测数", dataIndex: "records" },
  { title: "步数", dataIndex: "steps" },
  { title: "心率", dataIndex: "heartRate" },
  { title: "睡眠", dataIndex: "sleep" },
];

const stepsByDay = computed(() => {
  const result = new Map<string, number>();
  for (const event of stepSamples.value) {
    const key = dateInTimezone(event.start_at, reportTimezone.value);
    result.set(key, (result.get(key) ?? 0) + (event.payload.count?.value ?? 0));
  }
  return [...result.entries()].sort((a, b) => a[0].localeCompare(b[0]));
});

const heartRatePoints = computed(() => heartRateSamples.value.map((event) => ({
  time: formatUtcText(event.start_at),
  instant: Date.parse(event.start_at),
  bpm: event.payload.beats_per_minute ?? 0,
  origin: event.payload.data_origin,
})));

const stepsOption = computed<EChartsOption | null>(() => {
  if (!stepsByDay.value.length) return null;
  return {
    grid: { left: 64, right: 24, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: stepsByDay.value.map(([date]) => date.slice(5)) },
    yAxis: { type: "value" },
    series: [{ type: "bar", data: stepsByDay.value.map(([, value]) => value), color: "#1677ff", barMaxWidth: 20 }],
  };
});

const heartRateOption = computed<EChartsOption | null>(() => {
  if (heartRatePoints.value.length < 2) return null;
  return {
    grid: { left: 48, right: 24, top: 24, bottom: 32 },
    tooltip: {
      trigger: "axis",
      formatter: (params: unknown) => {
        const list = params as Array<{ dataIndex: number }>;
        const point = heartRatePoints.value[list[0]?.dataIndex ?? 0];
        return point ? `${point.time}<br/>${point.bpm} bpm · ${point.origin}` : "";
      },
    },
    xAxis: { type: "category", data: heartRatePoints.value.map((point) => point.time), axisLabel: { show: false } },
    yAxis: { type: "value", name: "bpm" },
    series: [{ type: "line", smooth: true, data: heartRatePoints.value.map((point) => point.bpm), color: "#52c41a", symbolSize: 3 }],
  };
});

const sleepColumns = [
  { title: "开始（UTC）", dataIndex: "start" },
  { title: "结束（UTC）", dataIndex: "end" },
  { title: "时长", dataIndex: "duration" },
  { title: "来源应用", dataIndex: "origin" },
  { title: "采集时区", dataIndex: "captureZone" },
  { title: "策略状态", dataIndex: "policyState" },
];

const sleepRows = computed(() => events.value.filter(isSleep).map((event, index) => {
  const startMs = Date.parse(event.start_at);
  const endMs = event.end_at ? Date.parse(event.end_at) : startMs;
  const withheld = withheldIds.value.has(event.event_id);
  return {
    key: `${event.event_id}-${event.revision}-${index}`,
    start: formatUtcText(event.start_at),
    end: event.end_at ? formatUtcText(event.end_at) : "缺失",
    duration: formatMinutes(Math.max(0, endMs - startMs) / 60_000),
    origin: event.payload.data_origin,
    captureZone: formatCaptureZone(event.capture_timezone, event.capture_offset_minutes),
    policyState: withheld ? `未计入（策略 v${context.value?.source_policy_version ?? 1}）` : "已计入",
  };
}));

const timelineColumns = [
  { title: "类型", dataIndex: "type" },
  { title: "开始（UTC）", dataIndex: "start" },
  { title: "结束（UTC）", dataIndex: "end" },
  { title: "观测值", dataIndex: "value" },
  { title: "来源应用（origin）", dataIndex: "origin" },
  { title: "设备", dataIndex: "device" },
  { title: "事件", dataIndex: "eventId", key: "eventId" },
  { title: "采集时区", dataIndex: "captureZone" },
  { title: "修订", dataIndex: "revision" },
  { title: "同步时间（UTC）", dataIndex: "synced" },
];

const timelineRows = computed(() => events.value.map((event, index) => ({
  key: `${event.event_id}-${event.revision}-${index}`,
  type: typeName(event.event_type),
  start: formatUtcText(event.start_at),
  end: event.end_at ? formatUtcText(event.end_at) : "瞬时",
  value: observationValue(event),
  origin: event.payload.data_origin,
  device: `${event.device.platform} · ${event.device.id}`,
  eventId: event.event_id.slice(0, 8),
  fullEventId: event.event_id,
  captureZone: formatCaptureZone(event.capture_timezone, event.capture_offset_minutes),
  revision: `修订 ${event.revision}`,
  synced: formatUtcText(event.provenance.observed_at),
})));

const provenanceText = computed(() => provenance.value.join("、"));

function typeName(eventType: string): string {
  if (eventType === "health.step.sample") return "步数";
  if (eventType === "health.heartrate.sample") return "心率";
  if (eventType === "health.sleep.session") return "睡眠";
  return eventType;
}

function observationValue(event: VersionedEvent): string {
  if (isStep(event)) return `${event.payload.count?.value ?? 0} 步`;
  if (isHeartRate(event)) return `${event.payload.beats_per_minute ?? 0} bpm`;
  if (isSleep(event)) {
    const start = Date.parse(event.start_at);
    const end = event.end_at ? Date.parse(event.end_at) : start;
    return formatMinutes(Math.max(0, end - start) / 60_000);
  }
  return "--";
}

async function load(cursor?: string) {
  const range = currentRangeUtc();
  if (!range) {
    error.value = "请选择查询日期范围。";
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    const page = await fetchHealthEvents(range.start, range.end, reportTimezone.value, cursor ? { cursor } : undefined);
    events.value = cursor ? [...events.value, ...page.data] : page.data;
    context.value = page.context;
    nextCursor.value = page.page.next_cursor;
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

async function initialize() {
  try {
    // The Owner report timezone defines report boundaries; the browser timezone is never used.
    reportTimezone.value = (await fetchOwnerSettings()).report_timezone;
  } catch {
    // Settings unavailable; the server still defaults to UTC.
  }
  const today = todayInTimezone(reportTimezone.value);
  endDate.value = dayjs(today);
  startDate.value = dayjs(addDaysIso(today, -6));
  await load();
}

initialize();
</script>

<style scoped>
.toolbar-panel {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.coverage-note {
  display: block;
  margin-top: 8px;
}

.load-more {
  margin-top: 12px;
}
</style>
