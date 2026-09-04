import { createRouter, createWebHistory } from "vue-router";
import DashboardView from "../views/DashboardView.vue";
import UsageView from "../views/UsageView.vue";
import HealthView from "../views/HealthView.vue";
import SpendingView from "../views/SpendingView.vue";
import ClassificationView from "../views/ClassificationView.vue";
import CredentialsView from "../views/CredentialsView.vue";
import SettingsView from "../views/SettingsView.vue";
import LoginView from "../views/LoginView.vue";
import SetupView from "../views/SetupView.vue";
import { useAuthStore } from "../stores/auth";

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", redirect: "/dashboard" },
    { path: "/dashboard", name: "dashboard", component: DashboardView, meta: { requiresAuth: true } },
    { path: "/usage", name: "usage", component: UsageView, meta: { requiresAuth: true } },
    { path: "/health", name: "health", component: HealthView, meta: { requiresAuth: true } },
    { path: "/spending", name: "spending", component: SpendingView, meta: { requiresAuth: true } },
    { path: "/classification", name: "classification", component: ClassificationView, meta: { requiresAuth: true } },
    { path: "/credentials", name: "credentials", component: CredentialsView, meta: { requiresAuth: true } },
    { path: "/settings", name: "settings", component: SettingsView, meta: { requiresAuth: true } },
    { path: "/login", name: "login", component: LoginView },
    { path: "/setup", name: "setup", component: SetupView },
  ],
});

router.beforeEach(async (to) => {
  const auth = useAuthStore();
  if (auth.phase === "loading") {
    await auth.initialize();
  }
  if (to.meta.requiresAuth && auth.phase !== "authenticated") {
    return { name: auth.phase === "setup" ? "setup" : "login" };
  }
  if (!to.meta.requiresAuth && auth.phase === "authenticated" && (to.name === "login" || to.name === "setup")) {
    return { path: "/dashboard" };
  }
  if (to.name === "login" && auth.phase === "setup") {
    return { name: "setup" };
  }
  return true;
});
