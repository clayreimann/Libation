import type { Bookmark } from "./api/types";
import type { ExportFormat } from "./api/client";

export const bookmarkFormats: { id: ExportFormat; label: string }[] = [
  { id: "csv", label: "CSV" },
  { id: "xlsx", label: "Excel (XLSX)" },
  { id: "json", label: "JSON" },
];

/** h:mm:ss, or m:ss under an hour. */
export function clock(sec: number): string {
  const t = Math.max(0, Math.floor(sec));
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const s = t % 60;
  const ss = String(s).padStart(2, "0");
  return h ? `${h}:${String(m).padStart(2, "0")}:${ss}` : `${m}:${ss}`;
}

export function bookmarkLine(b: Pick<Bookmark, "type" | "start" | "end">): string {
  return b.end != null && b.end > b.start ? `${b.type} ${clock(b.start)} to ${clock(b.end)}` : `${b.type} at ${clock(b.start)}`;
}

export function countLine(list: readonly Pick<Bookmark, "type">[]): string {
  if (!list.length) return "No clips, notes or bookmarks.";
  const counts = new Map<string, number>();
  for (const b of list) counts.set(b.type, (counts.get(b.type) ?? 0) + 1);
  return [...counts].map(([t, n]) => `${n} ${t.toLowerCase()}${n === 1 ? "" : "s"}`).join(", ");
}
