<template>
  <a-layout-header class="app-header">
    <div>
      <div class="page-title">{{ title }}</div>
      <div class="page-subtitle">服务端 {{ settings.apiBase }}</div>
    </div>
    <div class="header-actions">
      <a-tag :color="connectionColor">{{ connectionLabel }}</a-tag>
      <a-button size="small" @click="probe">测试连接</a-button>
      <a-switch
        :checked="settings.theme === 'dark'"
        checked-children="暗"
        un-checked-children="亮"
        @change="toggleTheme"
      />
    </div>
  </a-layout-header>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useRoute } from "vue-router";
import { fetchHealthProbe } from "../../api/dashboard";
import { useSettingsStore } from "../../stores/settings";

const route = useRoute();
const settings = useSettingsStore();
const connection = ref<"idle" | "ok" | "error">("idle");

const title = computed(() => {
  if (route.path.startsWith("/usage")) return "应用使用";
  if (route.path.startsWith("/health")) return "运动健康";
  if (route.path.startsWith("/settings")) return "设置";
  return "总览";
});

const connectionLabel = computed(() => {
  if (connection.value === "ok") return "已连接";
  if (connection.value === "error") return "连接失败";
  return "未测试";
});

const connectionColor = computed(() => {
  if (connection.value === "ok") return "green";
  if (connection.value === "error") return "red";
  return "default";
});

async function probe() {
  try {
    await fetchHealthProbe(settings.apiBase);
    connection.value = "ok";
  } catch {
    connection.value = "error";
  }
}

function toggleTheme(checked: boolean) {
  settings.setTheme(checked ? "dark" : "light");
}
</script>
