<template>
  <a-card class="device-card" :class="{ offline: !device.online }" :bordered="false">
    <div class="device-row">
      <div class="platform">
        <component :is="platformIcon" />
      </div>
      <div class="device-main">
        <div class="device-name">{{ device.device_name }}</div>
        <div class="device-app">{{ device.online ? currentText : "离线" }}</div>
      </div>
      <a-tag :color="device.online ? 'green' : 'default'">{{ device.online ? "在线" : "离线" }}</a-tag>
    </div>
    <div class="device-meta">
      {{ device.platform }} · {{ device.last_seen_minutes_ago }} 分钟前
    </div>
  </a-card>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { DesktopOutlined, LaptopOutlined, MobileOutlined } from "@ant-design/icons-vue";
import type { DeviceState } from "../../api/types";

const props = defineProps<{ device: DeviceState }>();

const platformIcon = computed(() => {
  if (props.device.platform === "android") return MobileOutlined;
  if (props.device.platform === "windows") return DesktopOutlined;
  return LaptopOutlined;
});

const currentText = computed(() => {
  if (props.device.is_afk) return "AFK";
  return props.device.current_app || "无前台应用";
});
</script>
