<template>
  <a-card class="device-card" :class="{ offline: !device.online }" :bordered="false">
    <div class="device-row">
      <div class="platform">
        <component :is="platformIcon" />
      </div>
      <div class="device-main">
        <div class="device-name">{{ device.device_name ?? device.device_id }}</div>
        <div class="device-app">{{ device.online ? currentText : "离线" }}</div>
      </div>
      <a-tag :color="device.online ? 'green' : 'default'">{{ device.online ? "在线" : "离线" }}</a-tag>
    </div>
    <div class="device-meta">
      {{ device.platform }} · {{ ageText }}
    </div>
  </a-card>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { DesktopOutlined, LaptopOutlined, MobileOutlined } from "@ant-design/icons-vue";
import type { DeviceStatus } from "../../generated/contract-models";

const props = defineProps<{ device: DeviceStatus }>();

const platformIcon = computed(() => {
  if (props.device.platform === "android") return MobileOutlined;
  if (props.device.platform === "windows") return DesktopOutlined;
  return LaptopOutlined;
});

const currentText = computed(() => {
  const activity = props.device.activity;
  if (activity?.is_afk) return "AFK";
  return activity?.application_label || activity?.application_id || "无前台应用";
});

const ageText = computed(() => {
  const seconds = props.device.age_seconds;
  if (seconds < 60) return `${seconds} 秒前`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} 分钟前`;
  return `${Math.floor(minutes / 60)} 小时前`;
});
</script>
