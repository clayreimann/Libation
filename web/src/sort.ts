import type { operations } from "./api/schema";
import { readPref, writePref } from "./prefs";

export type SortKey = NonNullable<operations["getLibrary"]["parameters"]["query"]>["sort"] & string;
export type SortDir = "asc" | "desc";
export interface SortState {
  key: SortKey;
  dir: SortDir;
}

export const sortKeys: readonly SortKey[] = ["title", "author", "narrator", "series", "length", "purchaseDate", "added", "status", "rating", "lastDownload"];

const KEY = "libation.sort";

/** Same column again flips the direction; a new column starts ascending. */
export function toggleSort(cur: SortState | null, key: SortKey): SortState {
  if (cur?.key === key) return { key, dir: cur.dir === "asc" ? "desc" : "asc" };
  return { key, dir: "asc" };
}

export function parseSort(v: unknown): SortState | null {
  if (typeof v !== "object" || v === null) return null;
  const { key, dir } = v as Record<string, unknown>;
  return sortKeys.includes(key as SortKey) && (dir === "asc" || dir === "desc") ? { key: key as SortKey, dir } : null;
}

export const loadSort = (): SortState | null => parseSort(readPref(KEY));
export const saveSort = (s: SortState | null): void => writePref(KEY, s);
