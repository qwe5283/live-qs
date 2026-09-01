<template>
  <div class="auth-page">
    <a-card class="auth-card" :bordered="false">
      <div class="auth-title">登录 LiveQs</div>
      <div class="auth-subtitle">输入 Owner 密码以访问个人数据</div>
      <a-form layout="vertical" @submit.prevent>
        <a-form-item label="密码">
          <a-input-password
            v-model:value="password"
            placeholder="Owner 密码"
            autofocus
            @keyup.enter="submit"
          />
        </a-form-item>
        <a-button type="primary" block :loading="loading" @click="submit">登录</a-button>
      </a-form>
      <a-alert
        v-if="error"
        class="auth-message"
        type="error"
        show-icon
        :message="error"
      />
    </a-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { useRouter } from "vue-router";
import { ApiError } from "../api/client";
import { useAuthStore } from "../stores/auth";

const router = useRouter();
const auth = useAuthStore();
const password = ref("");
const loading = ref(false);
const error = ref("");

async function submit() {
  if (!password.value) {
    error.value = "请输入密码";
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    await auth.login(password.value);
    password.value = "";
    await router.push({ path: "/dashboard" });
  } catch (err) {
    error.value = err instanceof ApiError && err.code === "invalid_credentials"
      ? "密码不正确"
      : err instanceof Error
        ? err.message
        : String(err);
  } finally {
    loading.value = false;
  }
}
</script>
