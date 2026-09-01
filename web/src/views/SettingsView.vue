<template>
  <div class="settings-page">
    <section class="panel settings-panel">
      <div class="panel-title">连接设置</div>
      <a-form layout="vertical">
        <a-form-item label="API Base URL">
          <a-input v-model:value="apiBaseInput" placeholder="http://localhost:8787" />
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

    <section class="panel settings-panel">
      <div class="panel-title">报表时区</div>
      <p class="settings-hint">
        报表时区定义日与周报告的边界，对所有浏览器和设备保持一致；浏览器所在时区不会改变同一份日报。
      </p>
      <a-form layout="vertical">
        <a-form-item label="报表时区（IANA 时区名）">
          <a-select
            v-model:value="reportTimezoneInput"
            show-search
            :options="timezoneOptions"
            placeholder="UTC"
          />
        </a-form-item>
        <a-button type="primary" :loading="savingTimezone" @click="saveReportTimezone">保存报表时区</a-button>
      </a-form>
      <a-alert
        v-if="timezoneMessage"
        class="settings-message"
        :type="timezoneMessageType"
        show-icon
        :message="timezoneMessage"
      />
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { fetchHealthProbe } from "../api/dashboard";
import { fetchOwnerSettings, updateOwnerSettings } from "../api/settings";
import { ianaTimezoneOptions } from "../utils/date";
import { useSettingsStore } from "../stores/settings";

const settings = useSettingsStore();
const apiBaseInput = ref(settings.apiBase);
const themeInput = ref(settings.theme);
const testing = ref(false);
const message = ref("");
const messageType = ref<"success" | "error" | "info">("info");
const themeOptions = [
  { label: "浅色", value: "light" },
  { label: "暗色", value: "dark" },
];

const reportTimezoneInput = ref<string>("UTC");
const savingTimezone = ref(false);
const timezoneMessage = ref("");
const timezoneMessageType = ref<"success" | "error">("success");
const timezoneOptions = ianaTimezoneOptions().map((zone) => ({ label: zone, value: zone }));

onMounted(async () => {
  try {
    reportTimezoneInput.value = (await fetchOwnerSettings()).report_timezone;
  } catch {
    // Keep the default until the settings endpoint answers.
  }
});

function save() {
  settings.setApiBase(apiBaseInput.value);
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

async function saveReportTimezone() {
  savingTimezone.value = true;
  timezoneMessage.value = "";
  try {
    const updated = await updateOwnerSettings({ report_timezone: reportTimezoneInput.value });
    reportTimezoneInput.value = updated.report_timezone;
    timezoneMessage.value = `报表时区已更新为 ${updated.report_timezone}。`;
    timezoneMessageType.value = "success";
  } catch (err) {
    timezoneMessage.value = err instanceof Error ? err.message : String(err);
    timezoneMessageType.value = "error";
  } finally {
    savingTimezone.value = false;
  }
}
</script>

<style scoped>
.settings-hint {
  margin: 0 0 12px;
  color: rgba(0, 0, 0, 0.45);
}
</style>
