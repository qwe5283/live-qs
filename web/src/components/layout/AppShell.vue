<template>
  <a-config-provider :theme="themeConfig">
    <a-layout class="app-shell">
      <a-layout-sider
        class="app-sidebar"
        :width="236"
        breakpoint="lg"
        collapsed-width="0"
      >
        <div class="brand">
          <div class="brand-mark">AI</div>
          <div>
            <div class="brand-title">AI Life</div>
            <div class="brand-subtitle">Personal context</div>
          </div>
        </div>
        <a-menu mode="inline" :selected-keys="[route.path]" @click="onMenuClick">
          <a-menu-item key="/dashboard">
            <DashboardOutlined />
            <span>总览</span>
          </a-menu-item>
          <a-menu-item key="/usage">
            <BarChartOutlined />
            <span>应用使用</span>
          </a-menu-item>
          <a-menu-item key="/health">
            <HeartOutlined />
            <span>运动健康</span>
          </a-menu-item>
          <a-menu-item key="/spending">
            <PayCircleOutlined />
            <span>消费支出</span>
          </a-menu-item>
          <a-menu-item key="/classification">
            <TagsOutlined />
            <span>语义分类</span>
          </a-menu-item>
          <a-menu-item key="/reclassification">
            <HistoryOutlined />
            <span>历史重分类</span>
          </a-menu-item>
          <a-menu-item key="/credentials">
            <KeyOutlined />
            <span>凭据管理</span>
          </a-menu-item>
          <a-menu-item key="/settings">
            <SettingOutlined />
            <span>设置</span>
          </a-menu-item>
        </a-menu>
      </a-layout-sider>

      <a-layout>
        <AppHeader />
        <a-layout-content class="app-content">
          <router-view />
        </a-layout-content>
      </a-layout>
    </a-layout>
  </a-config-provider>
</template>

<script setup lang="ts">
import { computed, onMounted, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import {
  BarChartOutlined,
  DashboardOutlined,
  HeartOutlined,
  HistoryOutlined,
  KeyOutlined,
  PayCircleOutlined,
  SettingOutlined,
  TagsOutlined,
} from "@ant-design/icons-vue";
import AppHeader from "./AppHeader.vue";
import { useSettingsStore } from "../../stores/settings";

const router = useRouter();
const route = useRoute();
const settings = useSettingsStore();

const themeConfig = computed(() => ({
  token: {
    colorPrimary: "#1677ff",
    borderRadius: 8,
    colorBgLayout: settings.theme === "dark" ? "#202026" : "#f5f5f5",
    colorBgContainer: settings.theme === "dark" ? "#292932" : "#ffffff",
    colorText: settings.theme === "dark" ? "#f0f0f0" : "#262626",
  },
}));

function onMenuClick(event: { key: string }) {
  router.push(event.key);
}

onMounted(() => settings.setTheme(settings.theme));
watch(() => settings.theme, (theme) => settings.setTheme(theme));
</script>
