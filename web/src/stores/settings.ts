import { defineStore } from "pinia";

const API_BASE_KEY = "ai_life_api_base";
const USER_TOKEN_KEY = "ai_life_user_token";
const THEME_KEY = "ai_life_theme";

export const useSettingsStore = defineStore("settings", {
  state: () => ({
    apiBase: localStorage.getItem(API_BASE_KEY) || "http://localhost:8787",
    userToken: localStorage.getItem(USER_TOKEN_KEY) || "",
    theme: (localStorage.getItem(THEME_KEY) || "light") as "light" | "dark",
  }),
  actions: {
    setApiBase(value: string) {
      this.apiBase = value.trim().replace(/\/+$/, "");
      localStorage.setItem(API_BASE_KEY, this.apiBase);
    },
    setUserToken(value: string) {
      this.userToken = value.trim();
      localStorage.setItem(USER_TOKEN_KEY, this.userToken);
    },
    setTheme(value: "light" | "dark") {
      this.theme = value;
      localStorage.setItem(THEME_KEY, value);
      document.documentElement.dataset.theme = value;
    },
  },
});
