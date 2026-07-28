import { createRouter, createWebHistory } from "vue-router";
import DashboardView from "../views/DashboardView.vue";
import UsageView from "../views/UsageView.vue";
import HealthView from "../views/HealthView.vue";
import SettingsView from "../views/SettingsView.vue";

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", redirect: "/dashboard" },
    { path: "/dashboard", name: "dashboard", component: DashboardView },
    { path: "/usage", name: "usage", component: UsageView },
    { path: "/health", name: "health", component: HealthView },
    { path: "/settings", name: "settings", component: SettingsView },
  ],
});
