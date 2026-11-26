import { readPref, writePref } from "./prefs";

export interface PlayerChapter {
  index: number;
  title: string;
  startSec: number;
  fileIndex: number;
  fileStartSec: number;
}

export const SPEEDS = [0.75, 1, 1.25, 1.5, 1.75, 2] as const;
export const SKIP_SEC = 30;
export const SAVE_INTERVAL_MS = 5000;
const RESTART_WINDOW_SEC = 3;
const FINISHED_TAIL_SEC = 10;

export function formatClock(sec: number): string {
  const s = Math.max(0, Math.floor(Number.isFinite(sec) ? sec : 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = String(s % 60).padStart(2, "0");
  return h ? `${h}:${String(m).padStart(2, "0")}:${ss}` : `${m}:${ss}`;
}

/** Index into `chapters` of the chapter playing at `time` within file `fileIndex`, or -1. */
export function chapterAt(chapters: readonly PlayerChapter[], fileIndex: number, time: number): number {
  let found = -1;
  chapters.forEach((c, i) => {
    if (c.fileIndex === fileIndex && c.fileStartSec <= time + 0.001) found = i;
  });
  return found;
}

export interface SeekTarget {
  fileIndex: number;
  time: number;
}

export const chapterTarget = (c: PlayerChapter): SeekTarget => ({ fileIndex: c.fileIndex, time: c.fileStartSec });

/** Previous chapter, or the start of the current one when more than a few seconds in. */
export function previousTarget(chapters: readonly PlayerChapter[], fileIndex: number, time: number): SeekTarget | undefined {
  const i = chapterAt(chapters, fileIndex, time);
  const cur = chapters[i];
  if (!cur) return undefined;
  const before = chapters[i - 1];
  return time - cur.fileStartSec > RESTART_WINDOW_SEC || !before ? chapterTarget(cur) : chapterTarget(before);
}

export function nextTarget(chapters: readonly PlayerChapter[], fileIndex: number, time: number): SeekTarget | undefined {
  const i = chapterAt(chapters, fileIndex, time);
  const n = i >= 0 ? chapters[i + 1] : undefined;
  return n && chapterTarget(n);
}

export const clampTime = (time: number, duration: number): number => {
  const t = Math.max(0, time);
  return Number.isFinite(duration) && duration > 0 ? Math.min(t, duration) : t;
};

export interface SavedPosition {
  file: number;
  time: number;
}

const lastFileKey = (asin: string) => `player.${asin}.file`;
const positionKey = (asin: string, file: number) => `player.${asin}.pos.${file}`;
const speedKey = "player.speed";

const savedAtKey = (asin: string) => `player.${asin}.at`;

export function savePosition(asin: string, file: number, time: number, now: number = Date.now()): void {
  writePref(lastFileKey(asin), file);
  writePref(positionKey(asin, file), Math.max(0, Math.round(time * 10) / 10));
  writePref(savedAtKey(asin), now);
}

/** When this browser last saved a position for the book, in ms since the epoch; undefined when never. */
export function loadSavedAt(asin: string): number | undefined {
  const v = readPref(savedAtKey(asin));
  return typeof v === "number" && Number.isFinite(v) ? v : undefined;
}

/** Maps a position in the whole book to a file and a time in it, using each file's duration. */
export function bookTarget(durations: readonly (number | null | undefined)[], positionSec: number): SeekTarget {
  let rest = Math.max(0, positionSec);
  for (let i = 0; i < durations.length; i++) {
    const d = durations[i];
    if (d == null || !Number.isFinite(d) || d <= 0) return { fileIndex: i, time: rest };
    if (rest < d || i === durations.length - 1) return { fileIndex: i, time: Math.min(rest, d) };
    rest -= d;
  }
  return { fileIndex: 0, time: 0 };
}

/**
 * Whether to start from Audible's position instead of this browser's: Audible has one, and it is
 * newer than the last local save (or there is none). A position that differs by under 30s is not worth a jump.
 */
export function preferAudible(audible: { positionMs: number; lastUpdated?: string | null } | null | undefined, localAt: number | undefined, localBookSec: number): boolean {
  if (!audible || audible.positionMs <= 0) return false;
  if (Math.abs(audible.positionMs / 1000 - localBookSec) < 30) return false;
  if (localAt === undefined) return true;
  const updated = audible.lastUpdated ? Date.parse(audible.lastUpdated) : NaN;
  return Number.isFinite(updated) && updated > localAt;
}

export function loadPosition(asin: string, fileCount: number): SavedPosition {
  const raw = readPref(lastFileKey(asin));
  const file = typeof raw === "number" && Number.isInteger(raw) && raw >= 0 && raw < fileCount ? raw : 0;
  const t = readPref(positionKey(asin, file));
  return { file, time: typeof t === "number" && Number.isFinite(t) && t > 0 ? t : 0 };
}

/** Saved time to resume from; restarts a file left within its last seconds. */
export function resumeTime(saved: number, duration: number): number {
  return Number.isFinite(duration) && duration > 0 && saved >= duration - FINISHED_TAIL_SEC ? 0 : saved;
}

/** Throttle: save when the interval has passed and the position actually moved. */
export function shouldSave(lastAt: number, now: number, lastTime: number, time: number, intervalMs = SAVE_INTERVAL_MS): boolean {
  return now - lastAt >= intervalMs && Math.abs(time - lastTime) >= 0.5;
}

export function loadSpeed(): number {
  const v = readPref(speedKey);
  return typeof v === "number" && (SPEEDS as readonly number[]).includes(v) ? v : 1;
}

export const saveSpeed = (v: number): void => writePref(speedKey, v);
