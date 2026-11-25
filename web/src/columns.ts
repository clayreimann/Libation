import { readPref, writePref } from "./prefs";
import type { SortKey } from "./sort";

export type ColumnId = "authors" | "narrators" | "series" | "length" | "status" | "myRating" | "rating" | "category" | "purchased" | "lastDownload" | "tags";

export interface Column {
  id: ColumnId;
  label: string;
  sort?: SortKey;
  defaultOn: boolean;
}

export const columns: readonly Column[] = [
  { id: "authors", label: "Authors", sort: "author", defaultOn: true },
  { id: "narrators", label: "Narrators", sort: "narrator", defaultOn: false },
  { id: "series", label: "Series", sort: "series", defaultOn: true },
  { id: "length", label: "Length", sort: "length", defaultOn: true },
  { id: "status", label: "Status", sort: "status", defaultOn: true },
  { id: "myRating", label: "My rating", defaultOn: false },
  { id: "rating", label: "Rating", sort: "rating", defaultOn: false },
  { id: "category", label: "Category", defaultOn: false },
  { id: "purchased", label: "Purchased", sort: "purchaseDate", defaultOn: true },
  { id: "lastDownload", label: "Last download", sort: "lastDownload", defaultOn: false },
  { id: "tags", label: "Tags", defaultOn: true },
];

const KEY = "libation.columns";

export const defaultVisible = (): ColumnId[] => columns.filter((c) => c.defaultOn).map((c) => c.id);

export function parseVisible(v: unknown): ColumnId[] {
  if (!Array.isArray(v)) return defaultVisible();
  const known = new Set<string>(columns.map((c) => c.id));
  return columns.filter((c) => v.includes(c.id) && known.has(c.id)).map((c) => c.id);
}

export function toggleColumn(visible: readonly ColumnId[], id: ColumnId): ColumnId[] {
  const on = new Set(visible);
  if (!on.delete(id)) on.add(id);
  return columns.filter((c) => on.has(c.id)).map((c) => c.id);
}

export const loadVisible = (): ColumnId[] => parseVisible(readPref(KEY));
export const saveVisible = (v: readonly ColumnId[]): void => writePref(KEY, v);
