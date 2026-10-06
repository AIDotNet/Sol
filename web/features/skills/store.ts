"use client";

import { create } from "zustand";
import * as api from "@/features/skills/api";

interface SkillsState {
  skills: api.Skill[];
  builtInSkills: api.BuiltInSkill[];
  sandboxEnabled: boolean;
  loading: boolean;
  loaded: boolean;
  error: string | null;
  load: () => Promise<void>;
  refresh: () => Promise<void>;
  install: (file: File, confirmDanger: boolean) => Promise<api.Skill>;
  remove: (id: string) => Promise<void>;
}

export const useSkillsStore = create<SkillsState>((set, get) => ({
  skills: [],
  builtInSkills: [],
  sandboxEnabled: false,
  loading: false,
  loaded: false,
  error: null,

  load: async () => {
    if (get().loading || get().loaded) return;
    await get().refresh();
  },

  refresh: async () => {
    set({ loading: true, error: null });
    try {
      // Built-ins are presentation-only metadata; failing to list them must not hide the
      // user's installed skills.
      const [result, builtIn] = await Promise.all([
        api.listSkills(),
        api.listBuiltInSkills().catch(() => []),
      ]);
      set({
        skills: result.skills,
        builtInSkills: builtIn,
        sandboxEnabled: result.sandboxEnabled,
        loading: false,
        loaded: true,
      });
    } catch (error) {
      set({
        loading: false,
        loaded: true,
        error: error instanceof Error ? error.message : "unknown",
      });
    }
  },

  install: async (file, confirmDanger) => {
    const skill = await api.installSkill(file, confirmDanger);
    await get().refresh();
    return skill;
  },

  remove: async (id) => {
    await api.deleteSkill(id);
    await get().refresh();
  },
}));
