import { defineStore } from "pinia";

const API_BASE_KEY = "ai_life_api_base";
const THEME_KEY = "ai_life_theme";
const LEGACY_USER_TOKEN_KEY = "ai_life_user_token";

export const useSettingsStore = defineStore("settings", {
  state: () => ({
    apiBase: localStorage.getItem(API_BASE_KEY) || "http://localhost:8787",
    theme: (localStorage.getItem(THEME_KEY) || "light") as "light" | "dark",
  }),
  actions: {
    setApiBase(value: string) {
      this.apiBase = value.trim().replace(/\/+$/, "");
      localStorage.setItem(API_BASE_KEY, this.apiBase);
    },
    setTheme(value: "light" | "dark") {
      this.theme = value;
      localStorage.setItem(THEME_KEY, value);
      document.documentElement.dataset.theme = value;
    },
  },
});

// Sessions now live in the server-managed HttpOnly cookie; drop tokens kept by older builds.
localStorage.removeItem(LEGACY_USER_TOKEN_KEY);
