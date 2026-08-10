"use client";

import { create } from "zustand";
import * as api from "@/features/ai/api";
import { findPreset } from "@/features/ai/presets";
import type { AiProvider, ModelCategory } from "@/features/ai/types";
import { enabledModels, resolveProtocol } from "@/features/ai/types";

/**
 * Provider, model and MCP configuration.
 *
 * The server is the source of truth — this store caches the last response and re-fetches after
 * every mutation rather than patching local state. Configuration changes are rare and the
 * payload is small, so a round trip is cheaper than the bugs that come from keeping two copies
 * of a nested structure in sync.
 */

export type SlotKind = "chat" | "fast" | "image" | "video";

/** Which provider and model to use for a given role. */
export interface ModelSlot {
  providerId: string | null;
  modelId: string | null;
}

const SLOT_STORAGE_KEY = "sol.model-slots";

const EMPTY_SLOT: ModelSlot = { providerId: null, modelId: null };

/** Built-in providers seeded automatically on first load, so a new device has them without the
 * user adding anything by hand. Keyed by builtinId; the flag below makes this a one-time offer —
 * a user who deletes the seeded provider should not have it come back on the next launch. */
const AUTO_SEEDED_BUILTIN_IDS = ["routin-ai"];

// A URL import and the canvas can request the initial provider load at the same time. Sharing
// the in-flight promise makes both callers wait for the complete list + best-effort seeding pass
// instead of racing an import against the seed operation.
let providerLoadPromise: Promise<void> | null = null;

function autoSeedStorageKey(builtinId: string): string {
  return `sol.auto-seeded.${builtinId}`;
}

function presetModelInput(
  model: NonNullable<ReturnType<typeof findPreset>>["defaultModels"][number],
): api.CreateModelInput {
  return {
    modelKey: model.modelKey,
    name: model.name,
    category: model.category,
    type: model.type ?? null,
    contextLength: model.contextLength ?? null,
    maxOutputTokens: model.maxOutputTokens ?? null,
    supportsVision: model.supportsVision ?? false,
    supportsFunctionCall: model.supportsFunctionCall ?? false,
    supportsThinking: model.supportsThinking ?? false,
    enabled: model.enabled ?? true,
  };
}

async function seedBuiltinProviders(
  providers: AiProvider[],
  createProvider: (input: api.CreateProviderInput) => Promise<AiProvider>,
): Promise<boolean> {
  if (typeof window === "undefined") return false;

  let seeded = false;

  for (const builtinId of AUTO_SEEDED_BUILTIN_IDS) {
    const storageKey = autoSeedStorageKey(builtinId);
    if (window.localStorage.getItem(storageKey)) continue;

    // Mark as offered before creating, not after: if creation fails, retrying on every reload
    // would be worse than the user occasionally missing a built-in provider they'd have to add
    // by hand.
    window.localStorage.setItem(storageKey, "1");

    if (providers.some((provider) => provider.builtinId === builtinId)) continue;

    const preset = findPreset(builtinId);
    if (!preset) continue;

    try {
      await createProvider({
        builtinId: preset.builtinId,
        name: preset.name,
        description: preset.description,
        type: preset.type,
        baseUrl: preset.defaultBaseUrl,
        presetVersion: preset.version,
        models: preset.defaultModels.map(presetModelInput),
      });
      seeded = true;
    } catch {
      // Best-effort: a name collision or transient network failure just means the provider
      // won't appear this time; the storage flag above prevents retrying on every reload.
    }
  }

  return seeded;
}

/** Adds defaults introduced after a built-in provider was created, without touching existing
 * model rows. The import endpoint is absent-only, so user edits remain authoritative. */
async function upgradeBuiltinProviderModels(providers: AiProvider[]): Promise<boolean> {
  let upgraded = false;

  for (const provider of providers) {
    const preset = findPreset(provider.builtinId);
    if (!preset) continue;

    // Built-in providers created before preset versions were persisted are the original v1.
    const currentVersion = provider.presetVersion ?? 1;
    if (currentVersion >= preset.version) continue;

    const newModels = preset.defaultModels.filter(
      (model) => (model.introducedInVersion ?? 1) > currentVersion,
    );

    try {
      await api.importConfig({
        providers: [
          {
            builtinId: preset.builtinId,
            name: provider.name,
            description: provider.description ?? null,
            icon: provider.icon ?? null,
            presetVersion: preset.version,
            type: provider.type,
            apiKey: null,
            baseUrl: provider.baseUrl,
            models: newModels.map(presetModelInput),
          },
        ],
      });
      upgraded = true;
    } catch {
      // Best-effort. Keeping the old version makes the next load retry the missing-model import.
    }
  }

  return upgraded;
}

interface AiState {
  providers: AiProvider[];
  mcpServers: api.McpServer[];
  loading: boolean;
  loaded: boolean;
  error: string | null;

  /**
   * Default provider+model per role.
   *
   * Kept in localStorage rather than on the server: this is a per-browser UI preference, and
   * storing it server-side would mean another table and another round trip for something the
   * user can re-pick in two clicks.
   */
  slots: Record<SlotKind, ModelSlot>;

  load: () => Promise<void>;
  refresh: () => Promise<void>;

  createProvider: (input: api.CreateProviderInput) => Promise<AiProvider>;
  updateProvider: (id: string, input: api.UpdateProviderInput) => Promise<void>;
  deleteProvider: (id: string) => Promise<void>;

  addModel: (providerId: string, input: api.CreateModelInput) => Promise<void>;
  updateModel: (modelId: string, input: api.UpdateModelInput) => Promise<void>;
  deleteModel: (modelId: string) => Promise<void>;
  setAllModelsEnabled: (providerId: string, enabled: boolean) => Promise<void>;

  createMcpServer: (input: api.CreateMcpServerInput) => Promise<void>;
  updateMcpServer: (id: string, input: Partial<api.CreateMcpServerInput>) => Promise<void>;
  deleteMcpServer: (id: string) => Promise<void>;
  importMcpServers: (servers: api.CreateMcpServerInput[]) => Promise<number>;

  setSlot: (kind: SlotKind, slot: ModelSlot) => void;
}

function readSlots(): Record<SlotKind, ModelSlot> {
  const fallback: Record<SlotKind, ModelSlot> = {
    chat: EMPTY_SLOT,
    fast: EMPTY_SLOT,
    image: EMPTY_SLOT,
    video: EMPTY_SLOT,
  };

  if (typeof window === "undefined") return fallback;

  try {
    const raw = window.localStorage.getItem(SLOT_STORAGE_KEY);
    if (!raw) return fallback;

    const parsed = JSON.parse(raw) as Partial<Record<SlotKind, ModelSlot>>;
    return {
      chat: parsed.chat ?? EMPTY_SLOT,
      fast: parsed.fast ?? EMPTY_SLOT,
      image: parsed.image ?? EMPTY_SLOT,
      video: parsed.video ?? EMPTY_SLOT,
    };
  } catch {
    return fallback;
  }
}

export const useAiStore = create<AiState>((set, get) => ({
  providers: [],
  mcpServers: [],
  loading: false,
  loaded: false,
  error: null,
  slots: readSlots(),

  /** Fetches once. Repeated calls while loaded are no-ops, so components can call it freely. */
  load: async () => {
    // `refresh` marks the store loaded before the best-effort preset seed finishes. Callers that
    // arrive during that window must still join the in-flight load, or a URL import can race the
    // seed create/refresh cycle.
    if (providerLoadPromise) {
      await providerLoadPromise;
      return;
    }

    if (get().loaded) return;

    providerLoadPromise = (async () => {
      await get().refresh();

      const providers = get().providers;
      const upgraded = await upgradeBuiltinProviderModels(providers);
      const seeded = await seedBuiltinProviders(providers, api.createProvider);
      if (upgraded || seeded) await get().refresh();
    })().finally(() => {
      providerLoadPromise = null;
    });

    await providerLoadPromise;
  },

  refresh: async () => {
    set({ loading: true, error: null });

    try {
      const [providers, mcp] = await Promise.all([
        api.listProviders(),
        api.listMcpServers(),
      ]);

      set({
        providers: providers.providers,
        mcpServers: mcp.servers,
        loading: false,
        loaded: true,
      });
    } catch (error) {
      const message =
        error instanceof api.AiApiError && error.isUnauthorized
          ? "unauthorized"
          : error instanceof Error
            ? error.message
            : "unknown";

      set({ loading: false, loaded: true, error: message });
    }
  },

  createProvider: async (input) => {
    const provider = await api.createProvider(input);
    await get().refresh();
    return provider;
  },

  updateProvider: async (id, input) => {
    await api.updateProvider(id, input);
    await get().refresh();
  },

  deleteProvider: async (id) => {
    await api.deleteProvider(id);

    // Drop any slot pointing at the provider that just disappeared, otherwise the canvas would
    // keep offering a model that no longer exists.
    const slots = { ...get().slots };
    let changed = false;
    for (const kind of Object.keys(slots) as SlotKind[]) {
      if (slots[kind].providerId === id) {
        slots[kind] = EMPTY_SLOT;
        changed = true;
      }
    }
    if (changed) {
      set({ slots });
      window.localStorage.setItem(SLOT_STORAGE_KEY, JSON.stringify(slots));
    }

    await get().refresh();
  },

  addModel: async (providerId, input) => {
    await api.addModel(providerId, input);
    await get().refresh();
  },

  updateModel: async (modelId, input) => {
    await api.updateModel(modelId, input);
    await get().refresh();
  },

  deleteModel: async (modelId) => {
    await api.deleteModel(modelId);
    await get().refresh();
  },

  setAllModelsEnabled: async (providerId, enabled) => {
    const provider = get().providers.find((p) => p.id === providerId);
    if (!provider) return;

    // Sequential rather than Promise.all: a provider can carry a few hundred models after a
    // catalog import, and firing that many concurrent requests would stall the connection pool.
    for (const model of provider.models) {
      if (model.enabled !== enabled) {
        await api.updateModel(model.id, { enabled });
      }
    }

    await get().refresh();
  },

  createMcpServer: async (input) => {
    await api.createMcpServer(input);
    await get().refresh();
  },

  updateMcpServer: async (id, input) => {
    await api.updateMcpServer(id, input);
    await get().refresh();
  },

  deleteMcpServer: async (id) => {
    await api.deleteMcpServer(id);
    await get().refresh();
  },

  importMcpServers: async (servers) => {
    const { added } = await api.importMcpServers(servers);
    await get().refresh();
    return added;
  },

  setSlot: (kind, slot) => {
    const slots = { ...get().slots, [kind]: slot };
    set({ slots });
    window.localStorage.setItem(SLOT_STORAGE_KEY, JSON.stringify(slots));
  },
}));

// --- selectors ---

/**
 * Providers that can actually serve a category: enabled, holding a key, and owning at least one
 * enabled model of that category. This is what node pickers should offer.
 */
export function usableProviders(
  providers: AiProvider[],
  category: ModelCategory,
): AiProvider[] {
  return providers.filter(
    (provider) =>
      provider.enabled &&
      // Ollama and other local runtimes need no key; the absence of one is only disqualifying
      // when the provider was configured to have one.
      (provider.hasApiKey || provider.builtinId === "ollama") &&
      enabledModels(provider, category).length > 0,
  );
}

/** Resolves a slot into the concrete provider, model and wire protocol, or null if unset/stale. */
export function resolveSlot(providers: AiProvider[], slot: ModelSlot) {
  if (!slot.providerId || !slot.modelId) return null;

  const provider = providers.find((p) => p.id === slot.providerId);
  if (!provider) return null;

  const model = provider.models.find((m) => m.id === slot.modelId);
  if (!model) return null;

  return { provider, model, protocol: resolveProtocol(provider, model) };
}
