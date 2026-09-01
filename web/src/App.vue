<template>
  <AppShell />
</template>

<script setup lang="ts">
import AppShell from "./components/layout/AppShell.vue";
import { onUnauthorized } from "./api/client";
import { useAuthStore } from "./stores/auth";
import { router } from "./router";

onUnauthorized(() => {
  const auth = useAuthStore();
  auth.invalidate();
  if (router.currentRoute.value.meta.requiresAuth) {
    void router.push({ name: "login" });
  }
});
</script>
