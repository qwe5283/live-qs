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
      <MetricCard title="步数" :value="summary?.steps.toLocaleString() ?? '--'" :icon="RiseOutlined" />
      <MetricCard title="睡眠" :value="summary ? formatMinutes(summary.sleep_minutes) : '--'" :icon="ClockCircleOutlined" />
      <MetricCard title="平均心率" :value="summary?.avg_heart_rate ? `${summary.avg_heart_rate} bpm` : '--'" :icon="HeartOutlined" />
      <MetricCard title="健康记录" :value="timeline?.records.length ?? '--'" :icon="DatabaseOutlined" />
    </div>

    <div class="dashboard-grid">
      <section class="panel">
        <div class="panel-title">步数趋势</div>
        <BaseChart v-if="stepsOption" :option="stepsOption" />
        <EmptyState v-else description="暂无步数数据" />
      </section>
      <section class="panel">
        <div class="panel-title">心率趋势</div>
        <BaseChart v-if="heartRateOption" :option="heartRateOption" />
        <EmptyState v-else description="暂无心率数据" />
      </section>
    </div>

    <section class="panel">
      <div class="panel-title">睡眠记录</div>
      <BaseChart v-if="sleepOption" :option="sleepOption" />
      <EmptyState v-else description="暂无睡眠数据" />
    </section>
  </div>
</template>

<script setup lang="ts">
import type { EChartsOption } from "echarts";
import dayjs, { type Dayjs } from "dayjs";
import { computed, ref } from "vue";
import { ClockCircleOutlined, DatabaseOutlined, HeartOutlined, RiseOutlined } from "@ant-design/icons-vue";
import { fetchHealthSummary, fetchHealthTimeline } from "../api/dashboard";
import type { HealthSummary, HealthTimelineResponse } from "../api/types";
import BaseChart from "../components/charts/BaseChart.vue";
import EmptyState from "../components/common/EmptyState.vue";
import MetricCard from "../components/common/MetricCard.vue";
import { endOfToday, formatMinutes, startOfDaysAgo } from "../utils/date";

const startValue = ref<Dayjs>(dayjs(startOfDaysAgo(6)));
const endValue = ref<Dayjs>(dayjs(endOfToday()));
const summary = ref<HealthSummary | null>(null);
const timeline = ref<HealthTimelineResponse | null>(null);
const loading = ref(false);
const error = ref("");

const stepsByDay = computed(() => {
  const result = new Map<string, number>();
  for (const record of timeline.value?.records ?? []) {
    if (record.type !== "health.steps" || record.value == null) continue;
    const key = record.start_at.slice(0, 10);
    result.set(key, (result.get(key) ?? 0) + record.value);
  }
  return [...result.entries()].sort((a, b) => a[0].localeCompare(b[0]));
});

const sleepByDay = computed(() => {
  const result = new Map<string, number>();
  for (const record of timeline.value?.records ?? []) {
    if (record.type !== "health.sleep") continue;
    const minutes = record.duration_minutes ?? record.value ?? 0;
    const key = record.start_at.slice(0, 10);
    result.set(key, (result.get(key) ?? 0) + minutes);
  }
  return [...result.entries()].sort((a, b) => a[0].localeCompare(b[0]));
});

const heartRates = computed(() => (timeline.value?.records ?? [])
  .filter((record) => record.type === "health.heart_rate" && record.value != null)
  .map((record) => ({
    time: new Date(record.start_at).toLocaleString(),
    value: record.value,
  })));

const stepsOption = computed<EChartsOption | null>(() => {
  if (!stepsByDay.value.length) return null;
  return {
    grid: { left: 52, right: 24, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: stepsByDay.value.map(([date]) => date.slice(5)) },
    yAxis: { type: "value" },
    series: [{ type: "bar", data: stepsByDay.value.map(([, value]) => Math.round(value)), color: "#1677ff", barMaxWidth: 20 }],
  };
});

const heartRateOption = computed<EChartsOption | null>(() => {
  if (heartRates.value.length < 2) return null;
  return {
    grid: { left: 48, right: 24, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: heartRates.value.map((item) => item.time), axisLabel: { show: false } },
    yAxis: { type: "value", name: "bpm" },
    series: [{ type: "line", smooth: true, data: heartRates.value.map((item) => item.value), color: "#52c41a", symbolSize: 3 }],
  };
});

const sleepOption = computed<EChartsOption | null>(() => {
  if (!sleepByDay.value.length) return null;
  return {
    grid: { left: 52, right: 24, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: sleepByDay.value.map(([date]) => date.slice(5)) },
    yAxis: { type: "value", axisLabel: { formatter: "{value}m" } },
    series: [{ type: "bar", data: sleepByDay.value.map(([, value]) => Math.round(value)), color: "#722ed1", barMaxWidth: 20 }],
  };
});

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const start = startValue.value.toDate();
    const end = endValue.value.endOf("day").toDate();
    const [summaryResponse, timelineResponse] = await Promise.all([
      fetchHealthSummary(start, end),
      fetchHealthTimeline(start, end),
    ]);
    summary.value = summaryResponse;
    timeline.value = timelineResponse;
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

load();
</script>
