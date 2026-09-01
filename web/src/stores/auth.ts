import { defineStore } from "pinia";
import { fetchOwnerSession, fetchOwnerStatus, loginOwner, logoutOwner, setupOwner } from "../api/auth";

export type AuthPhase = "loading" | "setup" | "login" | "authenticated";

export const useAuthStore = defineStore("auth", {
  state: () => ({
    phase: "loading" as AuthPhase,
  }),
  actions: {
    async initialize() {
      try {
        const status = await fetchOwnerStatus();
        if (!status.initialized) {
          this.phase = "setup";
          return;
        }
        await fetchOwnerSession();
        this.phase = "authenticated";
      } catch {
        this.phase = "login";
      }
    },
    async setup(password: string) {
      await setupOwner(password);
      this.phase = "authenticated";
    },
    async login(password: string) {
      await loginOwner(password);
      this.phase = "authenticated";
    },
    async logout() {
      await logoutOwner();
      this.phase = "login";
    },
    invalidate() {
      this.phase = "login";
    },
  },
});
