type Store = Pick<Storage, "getItem" | "setItem">;

export function defaultStore(): Store | undefined {
  try {
    return globalThis.localStorage;
  } catch {
    return undefined;
  }
}

export function readPref(key: string, store: Store | undefined = defaultStore()): unknown {
  try {
    const raw = store?.getItem(key);
    return raw == null ? undefined : JSON.parse(raw);
  } catch {
    return undefined;
  }
}

export function writePref(key: string, value: unknown, store: Store | undefined = defaultStore()): void {
  try {
    store?.setItem(key, JSON.stringify(value));
  } catch {
    // storage unavailable or full: preference lasts for this session only
  }
}
