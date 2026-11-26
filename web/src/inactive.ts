import type { InactiveBook, InactiveScan } from "./api/types";

export const isInactiveScanning = (s: Pick<InactiveScan, "state"> | null): boolean => s?.state === "running" || s?.state === "cancelling";

export function inactiveLine(s: InactiveScan, remaining = s.results?.length ?? 0): string {
  const progress = s.scanned != null ? `${s.scanned} of ${s.total} checked` : `${s.total} books to compare`;
  switch (s.state) {
    case "running":
      return `Scanning: ${progress}.`;
    case "cancelling":
      return `Cancelling: ${progress}.`;
    case "completed":
      return remaining ? `Finished: ${remaining} ${remaining === 1 ? "book is" : "books are"} no longer in the Audible account.` : "Finished: every compared book is still in the Audible account.";
    case "cancelled":
      return `Cancelled: ${remaining} found so far.`;
    case "failed":
      return `Failed: ${s.error ?? "the scan could not run"}`;
    default:
      return "No scan has run since the daemon started.";
  }
}

export const byTitle = (a: InactiveBook, b: InactiveBook): number => a.title.localeCompare(b.title);

/** Results minus books already trashed. */
export const remainingResults = (s: InactiveScan | null, trashed: ReadonlySet<string>): InactiveBook[] => (s?.results ?? []).filter((r) => !trashed.has(r.asin)).sort(byTitle);

export const toggleAllInactive = (selected: ReadonlySet<string>, results: readonly InactiveBook[]): Set<string> =>
  results.every((r) => selected.has(r.asin)) ? new Set() : new Set(results.map((r) => r.asin));
