import type { QualityApply, QualityResult, QualityScan } from "./api/types";

export const qualityLabel = (q: { codec: string; bitrate: number }): string => `${q.codec} ${q.bitrate} kbps`;

/** Percent more bitrate than the file has now; null when the current bitrate is unknown. */
export function bitrateGain(r: Pick<QualityResult, "currentQuality" | "availableQuality">): number | null {
  const cur = r.currentQuality.bitrate;
  return cur > 0 ? Math.round(((r.availableQuality.bitrate - cur) / cur) * 100) : null;
}

export const isScanning = (s: Pick<QualityScan, "state"> | null): boolean => s?.state === "running" || s?.state === "cancelling";

export function scanLine(s: QualityScan): string {
  const base = `${s.scanned} of ${s.total} scanned${s.failed ? `, ${s.failed} failed` : ""}`;
  switch (s.state) {
    case "running":
      return `Scanning: ${base}.`;
    case "cancelling":
      return `Cancelling: ${base}.`;
    case "completed":
      return `Finished: ${base}, ${s.results?.length ?? 0} with better quality available.`;
    case "cancelled":
      return `Cancelled: ${base}, ${s.results?.length ?? 0} found so far.`;
    case "failed":
      return `Failed: ${s.error ?? base}`;
    default:
      return "No scan has run since the daemon started.";
  }
}

export function applyLine(a: QualityApply): string {
  const parts = [`${a.marked} marked as not downloaded`];
  if (a.missing.length) parts.push(`${a.missing.length} not in the last results`);
  if (a.widevineEnabled) parts.push("Widevine turned on");
  if (a.xHeAacRequested) parts.push("xHE-AAC turned on");
  if (a.accountsMarkedForReRegistration) parts.push("accounts need signing in again before downloading");
  return `${parts.join(", ")}.`;
}

export function toggleAll(selected: ReadonlySet<string>, results: readonly QualityResult[]): Set<string> {
  return results.every((r) => selected.has(r.asin)) ? new Set() : new Set(results.map((r) => r.asin));
}
