const KEY = "libation.quickFilters";

type Store = Pick<Storage, "getItem" | "setItem">;

function defaultStore(): Store | undefined {
  try {
    return globalThis.localStorage;
  } catch {
    return undefined;
  }
}

export function loadFilters(store: Store | undefined = defaultStore()): string[] {
  try {
    const parsed: unknown = JSON.parse(store?.getItem(KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((f): f is string => typeof f === "string" && f.trim() !== "") : [];
  } catch {
    return [];
  }
}

export function saveFilters(filters: readonly string[], store: Store | undefined = defaultStore()): void {
  try {
    store?.setItem(KEY, JSON.stringify(filters));
  } catch {
    // storage unavailable or full: filters stay in memory for this session
  }
}

export function addFilter(filters: readonly string[], q: string): string[] {
  const t = q.trim();
  return !t || filters.includes(t) ? [...filters] : [...filters, t];
}

export function removeFilter(filters: readonly string[], index: number): string[] {
  return filters.filter((_, i) => i !== index);
}

export function moveFilter(filters: readonly string[], index: number, delta: -1 | 1): string[] {
  const to = index + delta;
  if (index < 0 || index >= filters.length || to < 0 || to >= filters.length) return [...filters];
  const next = [...filters];
  [next[index], next[to]] = [next[to]!, next[index]!];
  return next;
}
