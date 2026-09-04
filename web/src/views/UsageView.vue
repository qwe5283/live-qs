<template>
  <div class="page-stack">
    <a-alert v-if="error" type="error" show-icon :message="error" />

    <section class="toolbar-panel">
      <a-space wrap>
        <span>报表日</span>
        <a-date-picker v-model:value="reportDate" value-format="YYYY-MM-DD" :allow-clear="false" />
        <a-tag color="blue">报表时区：{{ reportTimezone }}</a-tag>
        <a-button type="primary" :loading="loading" @click="loadReport">查询</a-button>
      </a-space>
    </section>

    <SourcePolicyPanel :context="dayReport?.context ?? null" />

    <div class="metric-grid">
      <MetricCard
        title="设备时间（日）"
        :value="dayReport ? formatMinutes(dayReport.metrics.device_minutes) : '--'"
        note="各设备区间求和，可超过自然经过时间"
        :icon="DesktopOutlined"
      />
      <MetricCard
        title="活跃时间（日）"
        :value="dayReport ? formatMinutes(dayReport.metrics.active_minutes) : '--'"
        note="非 AFK 区间并集，重叠只计一次"
        :icon="AimOutlined"
      />
      <MetricCard
        title="设备时间（周）"
        :value="weekReport ? formatMinutes(weekReport.metrics.device_minutes) : '--'"
        :note="weekReport ? `${weekReport.week_start_date} ~ ${weekReport.week_end_date}` : undefined"
        :icon="FieldTimeOutlined"
      />
      <MetricCard
        title="活跃时间（周）"
        :value="weekReport ? formatMinutes(weekReport.metrics.active_minutes) : '--'"
        note="跨设备去重后的实际活跃覆盖"
        :icon="AppstoreOutlined"
      />
    </div>

    <div class="dashboard-grid">
      <section class="panel">
        <div class="panel-title">应用排行</div>
        <BaseChart v-if="appRankOption" :option="appRankOption" />
        <EmptyState v-else description="暂无应用使用数据" />
      </section>
      <section class="panel">
        <div class="panel-title">小时分布</div>
        <BaseChart v-if="hourlyOption" :option="hourlyOption" />
        <EmptyState v-else description="暂无小时分布" />
      </section>
    </div>

    <section class="panel">
      <div class="panel-title">设备轨道时间线（并发设备各自成轨）</div>
      <div v-if="lanes.length" class="lane-list">
        <div v-for="lane in lanes" :key="lane.deviceId" class="lane">
          <div class="lane-label">
            <span class="lane-device">{{ lane.deviceId }}</span>
            <a-tag>{{ lane.platform }}</a-tag>
            <span class="lane-metrics">设备 {{ formatMinutes(lane.deviceMinutes) }} · 活跃 {{ formatMinutes(lane.activeMinutes) }}</span>
          </div>
          <div class="lane-track">
            <div
              v-for="(bar, index) in lane.bars"
              :key="index"
              class="lane-bar"
              :class="{ afk: bar.afk }"
              :style="{ left: `${bar.leftPercent}%`, width: `${Math.max(bar.widthPercent, 0.5)}%` }"
              :title="bar.title"
            />
          </div>
        </div>
      </div>
      <EmptyState v-else description="暂无设备区间" />
    </section>

    <section class="panel">
      <div class="panel-title">周报（按日）</div>
      <a-table
        size="small"
        :pagination="false"
        :columns="weekColumns"
        :data-source="weekRows"
        row-key="date"
      />
    </section>

    <section class="panel">
      <div class="panel-title">
        活动区间（版本化事件）
        <a-tag v-if="eventContext === 'partial'" color="warning">数据不完整</a-tag>
      </div>
      <a-table
        size="small"
        :loading="loading"
        :pagination="{ pageSize: 8 }"
        :columns="eventColumns"
        :data-source="eventRows"
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
        v-if="nextEventCursor"
        size="small"
        :loading="loading"
        @click="loadMoreEvents"
      >加载更多</a-button>
    </section>
  </div>
</template>

<script setup lang="ts">
import type { EChartsOption } from "echarts";
import { computed, ref } from "vue";
import { AimOutlined, AppstoreOutlined, DesktopOutlined, FieldTimeOutlined } from "@ant-design/icons-vue";
import { fetchEvents } from "../api/events";
import { fetchUsageDayReport, fetchUsageWeekReport } from "../api/metrics";
import { fetchOwnerSettings } from "../api/settings";
import { fetchUsageApps, fetchUsageTimeline } from "../api/dashboard";
import type { VersionedEvent, UsageDayReport, UsageWeekReport } from "../generated/contract-models";
import type { UsageAppsResponse, UsageTimelineResponse } from "../api/types";
import BaseChart from "../components/charts/BaseChart.vue";
import EmptyState from "../components/common/EmptyState.vue";
import MetricCard from "../components/common/MetricCard.vue";
import SourcePolicyPanel from "../components/common/SourcePolicyPanel.vue";
import { formatCaptureZone, formatMinutes, formatUtcText, todayInTimezone } from "../utils/date";

const reportTimezone = ref("UTC");
const reportDate = ref("");
const dayReport = ref<UsageDayReport | null>(null);
const weekReport = ref<UsageWeekReport | null>(null);
const apps = ref<UsageAppsResponse | null>(null);
const timeline = ref<UsageTimelineResponse | null>(null);
const events = ref<VersionedEvent[]>([]);
const nextEventCursor = ref<string | null>(null);
const eventContext = ref<string>("");
const loading = ref(false);
const error = ref("");

const weekColumns = [
  { title: "日期", dataIndex: "date" },
  { title: "设备时间（分钟）", dataIndex: "deviceMinutes" },
  { title: "活跃时间（分钟）", dataIndex: "activeMinutes" },
];

const eventColumns = [
  { title: "开始（UTC）", dataIndex: "start" },
  { title: "结束（UTC）", dataIndex: "end" },
  { title: "应用", dataIndex: "app" },
  { title: "设备", dataIndex: "device" },
  { title: "事件", dataIndex: "eventId", key: "eventId" },
  { title: "采集时区", dataIndex: "captureZone" },
  { title: "时长", dataIndex: "duration" },
  { title: "状态", dataIndex: "state" },
  { title: "同步时间", dataIndex: "synced" },
];

const weekRows = computed(() => (weekReport.value?.days ?? []).map((day) => ({
  date: day.date,
  deviceMinutes: day.device_minutes,
  activeMinutes: day.active_minutes,
})));

const appRankOption = computed<EChartsOption | null>(() => {
  const top = (apps.value?.apps ?? []).slice(0, 10);
  if (!top.length) return null;
  return {
    grid: { left: 120, right: 24, top: 16, bottom: 28 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "value", axisLabel: { formatter: "{value}m" } },
    yAxis: { type: "category", data: top.map((item) => item.app), inverse: true },
    series: [{ type: "bar", data: top.map((item) => item.minutes), color: "#1677ff", barMaxWidth: 18 }],
  };
});

const hourlyOption = computed<EChartsOption | null>(() => {
  const top = apps.value?.apps[0];
  if (!top) return null;
  return {
    grid: { left: 44, right: 20, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: Array.from({ length: 24 }, (_, i) => `${i}:00`) },
    yAxis: { type: "value", name: "min" },
    series: [{ name: top.app, type: "bar", data: top.hourly_minutes.map(Math.round), color: "#52c41a", barMaxWidth: 14 }],
  };
});

interface LaneBar {
  leftPercent: number;
  widthPercent: number;
  afk: boolean;
  title: string;
}

const lanes = computed(() => {
  const report = dayReport.value;
  if (!report) return [];
  const startMs = Date.parse(report.context.from);
  const endMs = Date.parse(report.context.to);
  const span = Math.max(1, endMs - startMs);
  const laneMetrics = new Map(report.devices.map((device) => [device.device_id, device]));
  const groups = new Map<string, { platform: string; bars: LaneBar[] }>();
  for (const event of events.value) {
    const barStartMs = Math.max(Date.parse(event.start_at), startMs);
    const barEndMs = Math.min(event.end_at ? Date.parse(event.end_at) : Date.parse(event.start_at), endMs);
    if (barEndMs <= barStartMs) continue;
    const lane = groups.get(event.device.id) ?? { platform: event.device.platform, bars: [] };
    lane.bars.push({
      leftPercent: ((barStartMs - startMs) / span) * 100,
      widthPercent: ((barEndMs - barStartMs) / span) * 100,
      afk: event.payload.is_afk ?? false,
      title: [
        event.payload.application_label ?? event.payload.application_id,
        event.payload.is_afk ? "AFK" : "活跃",
        `${formatUtcText(event.start_at)} – ${event.end_at ? formatUtcText(event.end_at) : "进行中"}`,
      ].join(" · "),
    });
    groups.set(event.device.id, lane);
  }
  return [...groups].map(([deviceId, lane]) => ({
    deviceId,
    platform: lane.platform,
    bars: lane.bars,
    deviceMinutes: laneMetrics.get(deviceId)?.device_minutes ?? 0,
    activeMinutes: laneMetrics.get(deviceId)?.active_minutes ?? 0,
  }));
});

/** Observations the source policy withheld from normalized totals; they stay listed for traceability. */
const withheldEventIds = computed(() => new Set(
  (dayReport.value?.context.source_conflicts ?? []).flatMap((conflict) => conflict.competing_event_ids),
));

const eventRows = computed(() => events.value.map((event) => ({
  key: `${event.event_id}-${event.revision}`,
  start: formatUtcText(event.start_at),
  end: event.end_at ? formatUtcText(event.end_at) : "进行中",
  app: event.payload.application_label ?? event.payload.application_id,
  device: `${event.device.platform} · ${event.device.id}`,
  eventId: event.event_id.slice(0, 8),
  fullEventId: event.event_id,
  captureZone: formatCaptureZone(event.capture_timezone, event.capture_offset_minutes),
  duration: formatMinutes((event.payload.duration?.value ?? 0) / 60_000),
  state: [
    event.payload.is_afk ? "AFK" : "活跃",
    event.finalization_state === "final" ? "已结束" : "检查点",
    `修订 ${event.revision}`,
    withheldEventIds.value.has(event.event_id) ? "未计入（来源策略）" : null,
  ].filter(Boolean).join(" · "),
  synced: formatUtcText(event.provenance.observed_at),
})));

async function loadEvents(cursor?: string) {
  const report = dayReport.value;
  if (!report) return;
  const page = await fetchEvents(
    new Date(report.context.from),
    new Date(report.context.to),
    report.context.timezone,
    cursor ? { cursor } : undefined,
  );
  events.value = cursor ? [...events.value, ...page.data] : page.data;
  nextEventCursor.value = page.page.next_cursor;
  eventContext.value = page.context.completeness;
}

async function loadMoreEvents() {
  loading.value = true;
  error.value = "";
  try {
    await loadEvents(nextEventCursor.value ?? undefined);
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

async function loadLegacyCharts() {
  const report = dayReport.value;
  if (!report) return;
  const rangeStart = new Date(report.context.from);
  const rangeEnd = new Date(report.context.to);
  const [appsResponse, timelineResponse] = await Promise.all([
    fetchUsageApps(rangeStart, rangeEnd),
    fetchUsageTimeline(rangeStart, rangeEnd),
  ]);
  apps.value = appsResponse;
  timeline.value = timelineResponse;
}

/** Loads the day/week reports for the selected report day; boundaries always resolve on the server. */
async function loadReport() {
  if (!reportDate.value) return;
  loading.value = true;
  error.value = "";
  try {
    const [day, week] = await Promise.all([
      fetchUsageDayReport(reportDate.value),
      fetchUsageWeekReport(reportDate.value),
    ]);
    dayReport.value = day;
    weekReport.value = week;
    apps.value = null;
    timeline.value = null;
    events.value = [];
    nextEventCursor.value = null;
    await Promise.all([loadEvents(), loadLegacyCharts()]);
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

async function initialize() {
  loading.value = true;
  error.value = "";
  try {
    // The Owner report timezone defines report boundaries; the browser timezone is never used.
    reportTimezone.value = (await fetchOwnerSettings()).report_timezone;
  } catch {
    // Settings unavailable; the server still defaults to UTC.
  }
  reportDate.value = todayInTimezone(reportTimezone.value);
  await loadReport();
}

initialize();
</script>

<style scoped>
.lane-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.lane {
  display: grid;
  grid-template-columns: 260px 1fr;
  gap: 12px;
  align-items: center;
}

.lane-label {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.lane-device {
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.lane-metrics {
  color: rgba(0, 0, 0, 0.45);
  font-size: 12px;
  white-space: nowrap;
}

.lane-track {
  position: relative;
  height: 22px;
  border-radius: 4px;
  background: rgba(0, 0, 0, 0.04);
  overflow: hidden;
}

.lane-bar {
  position: absolute;
  top: 3px;
  height: 16px;
  border-radius: 3px;
  background: #1677ff;
  min-width: 2px;
}

.lane-bar.afk {
  background: rgba(0, 0, 0, 0.28);
}
</style>
