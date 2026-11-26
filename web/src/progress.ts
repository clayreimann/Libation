import type { BookProgress, ProgressPush } from "./api/types";
import type { BulkSummary } from "./bulk";
import { formatTime } from "./format";

export function clock(totalSec: number): string {
  const s = Math.max(0, Math.round(totalSec));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const pad = (n: number) => String(n).padStart(2, "0");
  return h ? `${h}:${pad(m)}:${pad(s % 60)}` : `${m}:${pad(s % 60)}`;
}

/** Percent of the book, or null when the length is unknown. */
export function percentOf(positionSec: number, lengthMinutes: number): number | null {
  return lengthMinutes > 0 ? Math.min(100, Math.round((positionSec / (lengthMinutes * 60)) * 100)) : null;
}

export interface ProgressRow {
  label: string;
  text: string;
}

export function progressRows(p: BookProgress, lengthMinutes: number): ProgressRow[] {
  const rows: ProgressRow[] = [];
  if (p.audible) {
    const sec = p.audible.positionMs / 1000;
    const pct = percentOf(sec, lengthMinutes);
    rows.push({ label: "Audible", text: `${clock(sec)}${pct == null ? "" : ` (${pct}%)`}, last listened ${formatTime(p.audible.lastUpdated)}` });
  } else {
    rows.push({ label: "Audible", text: "no position" });
  }
  if (p.audiobookshelf) {
    const a = p.audiobookshelf;
    const pct = percentOf(a.currentTimeSec, lengthMinutes);
    rows.push({ label: "Audiobookshelf", text: `${clock(a.currentTimeSec)}${pct == null ? "" : ` (${pct}%)`}${a.isFinished ? ", finished" : ""}, updated ${formatTime(a.lastUpdate)}` });
  } else {
    rows.push({ label: "Audiobookshelf", text: "not configured or no position" });
  }
  return rows;
}

// The API gives no structured skip code; only the reason text says the server is newer.
export const isNewerSkip = (r: ProgressPush): boolean => r.outcome === "skipped" && /newer/i.test(r.reason ?? "");

export interface PushView {
  kind: "ok" | "newer" | "info" | "bad";
  text: string;
}

export function pushView(r: ProgressPush): PushView {
  if (r.outcome === "pushed") return { kind: "ok", text: `Pushed${r.positionSec != null ? ` ${clock(r.positionSec)}` : ""} to Audiobookshelf.` };
  if (isNewerSkip(r)) return { kind: "newer", text: r.reason ?? "Audiobookshelf has newer progress." };
  if (r.outcome === "skipped") return { kind: "info", text: `Skipped: ${r.reason ?? "nothing to push"}.` };
  return { kind: "bad", text: `Push failed: ${r.reason ?? "unknown error"}` };
}

export function pushErrorText(status: number, message: string): string {
  return status === 409 ? "Save an Audiobookshelf server URL and token in Settings first." : status === 404 ? "The daemon does not know this book." : message;
}

/** Bulk push only queues jobs; each job's log holds pushed, skipped or failed. */
export function bulkPushLine(s: BulkSummary): string {
  const failed = s.failed.reduce((n, f) => n + f.asins.length, 0);
  const parts = [`${s.updated} queued`];
  if (s.conflict.length) parts.push(`${s.conflict.length} not liberated or already queued`);
  if (s.notFound.length) parts.push(`${s.notFound.length} unknown`);
  if (failed) parts.push(`${failed} failed`);
  return `${parts.join(", ")}.`;
}
