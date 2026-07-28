<template>
  <a-card class="metric-card" :bordered="false">
    <div class="metric-head">
      <span class="metric-title">{{ title }}</span>
      <component :is="icon" v-if="icon" class="metric-icon" />
    </div>
    <div class="metric-value">{{ value }}</div>
    <div v-if="note" class="metric-note">{{ note }}</div>
  </a-card>
</template>

<script setup lang="ts">
import type { Component } from "vue";

defineProps<{
  title: string;
  value: string | number;
  note?: string;
  icon?: Component;
}>();
</script>
