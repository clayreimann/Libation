import type { RemoveFromAudible } from "./api/types";

export const REMOVE_MAX = 100;

export const confirmPhrase = (n: number): string => `remove ${n} from audible`;

/** Exact match, as the server requires. */
export const phraseMatches = (typed: string, n: number): boolean => n > 0 && typed === confirmPhrase(n);

export interface Targets {
  targets: string[];
  excluded: number;
  overLimit: number;
}

/** Selected books that are Audible Plus, capped at the API limit; the rest are counted, never sent. */
export function plusTargets(selected: readonly string[], plus: ReadonlySet<string>): Targets {
  const all = selected.filter((a) => plus.has(a));
  return { targets: all.slice(0, REMOVE_MAX), excluded: selected.length - all.length, overLimit: Math.max(0, all.length - REMOVE_MAX) };
}

export interface RemovalSummary {
  removed: string[];
  notPlus: string[];
  notFound: string[];
  failed: { asin: string; error: string }[];
}

export function reduceRemoval(r: RemoveFromAudible): RemovalSummary {
  const s: RemovalSummary = { removed: [], notPlus: [], notFound: [], failed: [] };
  for (const i of r.results) {
    if (i.result === "removed") s.removed.push(i.asin);
    else if (i.result === "notPlus") s.notPlus.push(i.asin);
    else if (i.result === "notFound") s.notFound.push(i.asin);
    else s.failed.push({ asin: i.asin, error: i.error ?? "unknown error" });
  }
  return s;
}

export function removalLine(s: RemovalSummary): string {
  const parts = [`${s.removed.length} removed from Audible`];
  if (s.failed.length) parts.push(`${s.failed.length} failed`);
  if (s.notPlus.length) parts.push(`${s.notPlus.length} not Audible Plus`);
  if (s.notFound.length) parts.push(`${s.notFound.length} not found`);
  return `${parts.join(", ")}.`;
}
