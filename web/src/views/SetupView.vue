<template>
  <div class="auth-page">
    <a-card class="auth-card" :bordered="false">
      <div class="auth-title">初始化 LiveQs</div>
      <div class="auth-subtitle">首次使用：创建 Owner 密码（无需用户名）</div>
      <a-form layout="vertical" @submit.prevent>
        <a-form-item label="设置密码">
          <a-input-password
            v-model:value="password"
            placeholder="至少 8 个字符"
            autofocus
            @keyup.enter="submit"
          />
        </a-form-item>
        <a-form-item label="确认密码">
          <a-input-password
            v-model:value="confirmation"
            placeholder="再次输入密码"
            @keyup.enter="submit"
          />
        </a-form-item>
        <a-button type="primary" block :loading="loading" @click="submit">创建并登录</a-button>
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
const confirmation = ref("");
const loading = ref(false);
const error = ref("");

async function submit() {
  if (password.value.length < 8) {
    error.value = "密码至少需要 8 个字符";
    return;
  }
  if (password.value !== confirmation.value) {
    error.value = "两次输入的密码不一致";
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    await auth.setup(password.value);
    password.value = "";
    confirmation.value = "";
    await router.push({ path: "/dashboard" });
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}
</script>
