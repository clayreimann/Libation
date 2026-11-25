import type { Book } from "./api/types";

export type ParentRow = { kind: "parent"; parent: Book; episodes: Book[] };
export type Row = { kind: "book"; book: Book } | ParentRow;

/** Nests episodes under their parent at the first episode's position; an episode without a listed parent stays standalone. */
export function groupPodcasts(items: readonly Book[], parents: readonly Book[]): Row[] {
  const byAsin = new Map(parents.map((p) => [p.asin, p]));
  const groups = new Map<string, ParentRow>();
  const rows: Row[] = [];
  for (const book of items) {
    const parent = book.parentAsin ? byAsin.get(book.parentAsin) : undefined;
    if (!parent) {
      rows.push({ kind: "book", book });
      continue;
    }
    const group = groups.get(parent.asin);
    if (group) group.episodes.push(book);
    else {
      const created: ParentRow = { kind: "parent", parent, episodes: [book] };
      groups.set(parent.asin, created);
      rows.push(created);
    }
  }
  return rows;
}

export const needsParents = (items: readonly Book[]): boolean => items.some((b) => b.parentAsin);
