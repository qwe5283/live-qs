<template>
  <div class="page-stack">
    <a-alert
      v-if="error"
      type="error"
      show-icon
      :message="error"
      class="page-alert"
    />

    <div class="metric-grid">
      <MetricCard title="今日屏幕时长" :value="screenTime" :icon="DesktopOutlined" />
      <MetricCard title="专注时长" :value="focusTime" :note="focusRatio" :icon="AimOutlined" />
      <MetricCard title="今日步数" :value="current?.today.steps.toLocaleString() ?? '--'" :icon="RiseOutlined" />
      <MetricCard title="睡眠" :value="sleepTime" :icon="ClockCircleOutlined" />
    </div>

    <div class="dashboard-grid">
      <section class="panel">
        <div class="panel-title">设备状态</div>
        <div v-if="current?.devices.length" class="device-list">
          <DeviceCard v-for="device in current.devices" :key="device.device_id" :device="device" />
        </div>
        <EmptyState v-else description="暂无设备上报" />
      </section>

      <section class="panel">
        <div class="panel-title">今日 Top Apps</div>
        <BaseChart v-if="topAppsOption" :option="topAppsOption" />
        <EmptyState v-else description="暂无应用使用数据" />
      </section>
    </div>

    <section class="panel">
      <div class="panel-head">
        <div>
          <div class="panel-title">健康趋势</div>
          <div class="panel-subtitle">今日 Health Connect 数据</div>
        </div>
        <a-button size="small" :loading="loading" @click="load">刷新</a-button>
      </div>
      <BaseChart v-if="healthOption" :option="healthOption" />
      <EmptyState v-else description="暂无健康数据" />
    </section>
  </div>
</template>

<script setup lang="ts">
import type { EChartsOption } from "echarts";
import { computed, onMounted, onUnmounted, ref } from "vue";
import { AimOutlined, ClockCircleOutlined, DesktopOutlined, RiseOutlined } from "@ant-design/icons-vue";
import { fetchCurrent, fetchHealthTimeline } from "../api/dashboard";
import type { CurrentContext, HealthTimelineResponse } from "../api/types";
import BaseChart from "../components/charts/BaseChart.vue";
import DeviceCard from "../components/common/DeviceCard.vue";
import EmptyState from "../components/common/EmptyState.vue";
import MetricCard from "../components/common/MetricCard.vue";
import { endOfToday, formatMinutes, startOfToday } from "../utils/date";

const current = ref<CurrentContext | null>(null);
const health = ref<HealthTimelineResponse | null>(null);
const loading = ref(false);
const error = ref("");
let timer: number | undefined;

const screenTime = computed(() => current.value ? formatMinutes(current.value.today.active_screen_minutes) : "--");
const focusTime = computed(() => current.value ? formatMinutes(current.value.today.focus_minutes) : "--");
const sleepTime = computed(() => current.value ? formatMinutes(current.value.today.sleep_minutes) : "--");
const focusRatio = computed(() => {
  const today = current.value?.today;
  if (!today || today.active_screen_minutes <= 0) return undefined;
  return `${Math.round((today.focus_minutes / today.active_screen_minutes) * 100)}% of screen time`;
});

const topAppsOption = computed<EChartsOption | null>(() => {
  const apps = current.value?.today.top_apps ?? [];
  if (apps.length === 0) return null;
  return {
    grid: { left: 120, right: 20, top: 10, bottom: 24 },
    xAxis: { type: "value", axisLabel: { formatter: "{value}m" } },
    yAxis: { type: "category", data: apps.map((item) => item.app), inverse: true },
    tooltip: { trigger: "axis" },
    series: [{ type: "bar", data: apps.map((item) => item.minutes), color: "#1677ff", barMaxWidth: 18 }],
  };
});

const healthOption = computed<EChartsOption | null>(() => {
  const records = health.value?.records ?? [];
  const heartRates = records.filter((item) => item.type === "health.heart_rate" && item.value != null);
  if (heartRates.length < 2) return null;
  return {
    grid: { left: 48, right: 24, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: heartRates.map((item) => new Date(item.start_at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })) },
    yAxis: { type: "value", name: "bpm" },
    series: [{ type: "line", smooth: true, data: heartRates.map((item) => item.value), color: "#52c41a", symbolSize: 4 }],
  };
});

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [currentResponse, healthResponse] = await Promise.all([
      fetchCurrent(),
      fetchHealthTimeline(startOfToday(), endOfToday()),
    ]);
    current.value = currentResponse;
    health.value = healthResponse;
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

onMounted(() => {
  load();
  timer = window.setInterval(load, 10_000);
});

onUnmounted(() => {
  if (timer) window.clearInterval(timer);
});
</script>
