import type { QuickFilter, QuickFilters } from "./api/types";

const LEGACY_KEY = "libation.quickFilters";

type Store = Pick<Storage, "getItem" | "setItem" | "removeItem">;

function defaultStore(): Store | undefined {
  try {
    return globalThis.localStorage;
  } catch {
    return undefined;
  }
}

/** Filters an earlier build kept in this browser; read once for migration. */
export function loadLegacyFilters(store: Store | undefined = defaultStore()): string[] {
  try {
    const parsed: unknown = JSON.parse(store?.getItem(LEGACY_KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((f): f is string => typeof f === "string" && f.trim() !== "") : [];
  } catch {
    return [];
  }
}

export function clearLegacyFilters(store: Store | undefined = defaultStore()): void {
  try {
    store?.removeItem(LEGACY_KEY);
  } catch {
    // storage unavailable: the offer reappears next load
  }
}

/** Browser-local filters to offer for import: only when the server has none. */
export function migrationOffer(server: readonly QuickFilter[], legacy: readonly string[]): string[] {
  return server.length === 0 ? [...new Set(legacy)] : [];
}

export const toNamed = (filters: readonly string[]): QuickFilter[] => filters.map((filter) => ({ filter }));

export const label = (f: QuickFilter): string => f.name?.trim() || f.filter;

/** Desktop's "use default": the first filter applies on load. */
export function defaultQuery(qf: QuickFilters | null): string {
  return qf?.useDefault && qf.filters[0] ? qf.filters[0].filter : "";
}

export function addFilter(filters: readonly QuickFilter[], q: string): QuickFilter[] {
  const t = q.trim();
  return !t || filters.some((f) => f.filter === t) ? [...filters] : [...filters, { filter: t }];
}

export function removeFilter(filters: readonly QuickFilter[], index: number): QuickFilter[] {
  return filters.filter((_, i) => i !== index);
}

export function updateFilter(filters: readonly QuickFilter[], index: number, patch: Partial<QuickFilter>): QuickFilter[] {
  return filters.map((f, i) => (i === index ? { ...f, ...patch } : f));
}

export function moveFilter(filters: readonly QuickFilter[], index: number, delta: -1 | 1): QuickFilter[] {
  const to = index + delta;
  if (index < 0 || index >= filters.length || to < 0 || to >= filters.length) return [...filters];
  const next = [...filters];
  [next[index], next[to]] = [next[to]!, next[index]!];
  return next;
}

/** Splits the API's `filters[2]: message` 400 into the row it names. */
export function parseFilterError(message: string): { index: number; message: string } | null {
  const m = /^filters\[(\d+)\]:\s*([\s\S]*)$/.exec(message);
  return m ? { index: Number(m[1]), message: m[2]! } : null;
}
