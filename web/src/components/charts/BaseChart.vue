<template>
  <div ref="container" class="base-chart" />
</template>

<script setup lang="ts">
import * as echarts from "echarts";
import type { EChartsOption } from "echarts";
import { onBeforeUnmount, onMounted, ref, watch } from "vue";

const props = defineProps<{ option: EChartsOption }>();
const container = ref<HTMLDivElement | null>(null);
let chart: echarts.ECharts | null = null;
let observer: ResizeObserver | null = null;

onMounted(() => {
  if (!container.value) return;
  chart = echarts.init(container.value);
  chart.setOption(props.option);
  observer = new ResizeObserver(() => chart?.resize());
  observer.observe(container.value);
});

watch(
  () => props.option,
  (option) => chart?.setOption(option, true),
  { deep: true },
);

onBeforeUnmount(() => {
  observer?.disconnect();
  chart?.dispose();
});
</script>
