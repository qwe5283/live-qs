<template>
  <div class="settings-page">
    <section class="panel settings-panel">
      <div class="panel-title">连接设置</div>
      <a-form layout="vertical">
        <a-form-item label="API Base URL">
          <a-input v-model:value="apiBaseInput" placeholder="http://localhost:8787" />
        </a-form-item>
        <a-form-item label="User Token">
          <a-input-password v-model:value="tokenInput" />
        </a-form-item>
        <a-form-item label="主题">
          <a-segmented v-model:value="themeInput" :options="themeOptions" />
        </a-form-item>
        <a-space>
          <a-button type="primary" @click="save">保存</a-button>
          <a-button :loading="testing" @click="testConnection">测试连接</a-button>
        </a-space>
      </a-form>
      <a-alert
        v-if="message"
        class="settings-message"
        :type="messageType"
        show-icon
        :message="message"
      />
    </section>
  </div>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { fetchHealthProbe } from "../api/dashboard";
import { useSettingsStore } from "../stores/settings";

const settings = useSettingsStore();
const apiBaseInput = ref(settings.apiBase);
const tokenInput = ref(settings.userToken);
const themeInput = ref(settings.theme);
const testing = ref(false);
const message = ref("");
const messageType = ref<"success" | "error" | "info">("info");
const themeOptions = [
  { label: "浅色", value: "light" },
  { label: "暗色", value: "dark" },
];

function save() {
  settings.setApiBase(apiBaseInput.value);
  settings.setUserToken(tokenInput.value);
  settings.setTheme(themeInput.value as "light" | "dark");
  message.value = "设置已保存";
  messageType.value = "success";
}

async function testConnection() {
  testing.value = true;
  message.value = "";
  try {
    await fetchHealthProbe(apiBaseInput.value);
    message.value = "服务端可达";
    messageType.value = "success";
  } catch (err) {
    message.value = err instanceof Error ? err.message : String(err);
    messageType.value = "error";
  } finally {
    testing.value = false;
  }
}
</script>
