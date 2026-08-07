"use client";

import { create } from "zustand";
import * as api from "@/features/skills/api";

interface SkillsState {
  skills: api.Skill[];
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
      const result = await api.listSkills();
      set({
        skills: result.skills,
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
