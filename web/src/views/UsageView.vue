<template>
  <div class="page-stack">
    <a-alert v-if="error" type="error" show-icon :message="error" />

    <section class="toolbar-panel">
      <a-space wrap>
        <a-date-picker v-model:value="startValue" />
        <a-date-picker v-model:value="endValue" />
        <a-button type="primary" :loading="loading" @click="load">查询</a-button>
      </a-space>
    </section>

    <div class="metric-grid">
      <MetricCard title="总屏幕时长" :value="summary ? formatMinutes(summary.active_screen_minutes) : '--'" :icon="DesktopOutlined" />
      <MetricCard title="专注时长" :value="summary ? formatMinutes(summary.focus_minutes) : '--'" :icon="AimOutlined" />
      <MetricCard title="应用数" :value="apps?.apps.length ?? '--'" :icon="AppstoreOutlined" />
      <MetricCard title="时间片" :value="timeline?.segments.length ?? '--'" :icon="FieldTimeOutlined" />
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
      <div class="panel-title">实时前台时间线</div>
      <a-table
        size="small"
        :pagination="{ pageSize: 8 }"
        :columns="columns"
        :data-source="timelineRows"
        row-key="key"
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
      />
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
import dayjs, { type Dayjs } from "dayjs";
import { computed, ref } from "vue";
import { AimOutlined, AppstoreOutlined, DesktopOutlined, FieldTimeOutlined } from "@ant-design/icons-vue";
import { fetchEvents } from "../api/events";
import { fetchUsageApps, fetchUsageSummary, fetchUsageTimeline } from "../api/dashboard";
import type { ActivityIntervalEventV1 } from "../generated/contract-models";
import type { UsageAppsResponse, UsageSummary, UsageTimelineResponse } from "../api/types";
import BaseChart from "../components/charts/BaseChart.vue";
import EmptyState from "../components/common/EmptyState.vue";
import MetricCard from "../components/common/MetricCard.vue";
import { endOfToday, formatMinutes, startOfToday } from "../utils/date";

const startValue = ref<Dayjs>(dayjs(startOfToday()));
const endValue = ref<Dayjs>(dayjs(endOfToday()));
const summary = ref<UsageSummary | null>(null);
const apps = ref<UsageAppsResponse | null>(null);
const timeline = ref<UsageTimelineResponse | null>(null);
const events = ref<ActivityIntervalEventV1[]>([]);
const nextEventCursor = ref<string | null>(null);
const eventContext = ref<string>("");
const loading = ref(false);
const error = ref("");

const columns = [
  { title: "开始", dataIndex: "start" },
  { title: "设备", dataIndex: "device" },
  { title: "应用", dataIndex: "app" },
  { title: "时长", dataIndex: "minutes" },
  { title: "状态", dataIndex: "state" },
];

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

const timelineRows = computed(() => (timeline.value?.segments ?? []).map((item, index) => ({
  key: `${item.start_at}-${index}`,
  start: new Date(item.start_at).toLocaleString(),
  device: item.device_id,
  app: item.app,
  minutes: formatMinutes(item.minutes),
  state: item.is_afk ? "AFK" : item.category || "active",
})));

const eventColumns = [
  { title: "开始", dataIndex: "start" },
  { title: "结束", dataIndex: "end" },
  { title: "应用", dataIndex: "app" },
  { title: "设备", dataIndex: "device" },
  { title: "时长", dataIndex: "duration" },
  { title: "状态", dataIndex: "state" },
  { title: "同步时间", dataIndex: "synced" },
];

const eventRows = computed(() => events.value.map((event) => ({
  key: `${event.event_id}-${event.revision}`,
  start: new Date(event.start_at).toLocaleString(),
  end: event.end_at ? new Date(event.end_at).toLocaleString() : "进行中",
  app: event.payload.application_label ?? event.payload.application_id,
  device: event.device.id,
  duration: formatMinutes(event.payload.duration.value / 60_000),
  state: [
    event.payload.is_afk ? "AFK" : "活跃",
    event.finalization_state === "final" ? "已结束" : "检查点",
    `修订 ${event.revision}`,
  ].join(" · "),
  synced: new Date(event.provenance.observed_at).toLocaleString(),
})));

async function loadEvents(cursor?: string) {
  const start = startValue.value.toDate();
  const end = endValue.value.endOf("day").toDate();
  const page = await fetchEvents(start, end, cursor ? { cursor } : undefined);
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

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const start = startValue.value.toDate();
    const end = endValue.value.endOf("day").toDate();
    const [summaryResponse, appsResponse, timelineResponse] = await Promise.all([
      fetchUsageSummary(start, end),
      fetchUsageApps(start, end),
      fetchUsageTimeline(start, end),
    ]);
    summary.value = summaryResponse;
    apps.value = appsResponse;
    timeline.value = timelineResponse;
    await loadEvents();
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

load();
</script>
