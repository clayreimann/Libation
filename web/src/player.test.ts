import { beforeEach, describe, expect, it, vi } from "vitest";
import { chapterAt, clampTime, formatClock, loadPosition, nextTarget, previousTarget, resumeTime, savePosition, shouldSave, type PlayerChapter } from "./player";

const mem = new Map<string, string>();
vi.stubGlobal("localStorage", {
  getItem: (k: string) => mem.get(k) ?? null,
  setItem: (k: string, v: string) => void mem.set(k, v),
});

const ch = (index: number, fileIndex: number, fileStartSec: number): PlayerChapter => ({ index, title: `C${index}`, startSec: 0, fileIndex, fileStartSec });
const chapters = [ch(0, 0, 0), ch(1, 0, 100), ch(2, 0, 200), ch(3, 1, 0), ch(4, 1, 50)];

beforeEach(() => mem.clear());

describe("formatClock", () => {
  it("formats minutes and hours", () => {
    expect(formatClock(0)).toBe("0:00");
    expect(formatClock(65.9)).toBe("1:05");
    expect(formatClock(3661)).toBe("1:01:01");
  });
  it("tolerates bad input", () => {
    expect(formatClock(NaN)).toBe("0:00");
    expect(formatClock(-5)).toBe("0:00");
  });
});

describe("chapterAt", () => {
  it("finds the chapter containing the time within the file", () => {
    expect(chapterAt(chapters, 0, 0)).toBe(0);
    expect(chapterAt(chapters, 0, 99.9)).toBe(0);
    expect(chapterAt(chapters, 0, 100)).toBe(1);
    expect(chapterAt(chapters, 0, 500)).toBe(2);
    expect(chapterAt(chapters, 1, 60)).toBe(4);
  });
  it("returns -1 with no chapters for the file", () => {
    expect(chapterAt([], 0, 5)).toBe(-1);
    expect(chapterAt(chapters, 2, 5)).toBe(-1);
  });
});

describe("chapter navigation", () => {
  it("next crosses files", () => {
    expect(nextTarget(chapters, 0, 250)).toEqual({ fileIndex: 1, time: 0 });
    expect(nextTarget(chapters, 1, 60)).toBeUndefined();
  });
  it("previous restarts the chapter when far in, else goes back", () => {
    expect(previousTarget(chapters, 0, 150)).toEqual({ fileIndex: 0, time: 100 });
    expect(previousTarget(chapters, 0, 101)).toEqual({ fileIndex: 0, time: 0 });
    expect(previousTarget(chapters, 1, 1)).toEqual({ fileIndex: 0, time: 200 });
    expect(previousTarget(chapters, 0, 1)).toEqual({ fileIndex: 0, time: 0 });
  });
});

describe("positions", () => {
  it("round-trips per book and file", () => {
    savePosition("B1", 1, 42.26);
    expect(loadPosition("B1", 3)).toEqual({ file: 1, time: 42.3 });
    expect(loadPosition("B2", 3)).toEqual({ file: 0, time: 0 });
  });
  it("ignores a saved file that no longer exists", () => {
    savePosition("B1", 5, 10);
    expect(loadPosition("B1", 2)).toEqual({ file: 0, time: 0 });
  });
  it("ignores corrupt values", () => {
    mem.set("player.B1.file", '"x"');
    mem.set("player.B1.pos.0", "{}");
    expect(loadPosition("B1", 2)).toEqual({ file: 0, time: 0 });
  });
});

describe("resumeTime / clampTime / shouldSave", () => {
  it("restarts a finished file", () => {
    expect(resumeTime(95, 100)).toBe(0);
    expect(resumeTime(50, 100)).toBe(50);
    expect(resumeTime(50, NaN)).toBe(50);
  });
  it("clamps", () => {
    expect(clampTime(-3, 100)).toBe(0);
    expect(clampTime(150, 100)).toBe(100);
    expect(clampTime(150, NaN)).toBe(150);
  });
  it("throttles saves", () => {
    expect(shouldSave(0, 1000, 0, 5)).toBe(false);
    expect(shouldSave(0, 6000, 0, 5)).toBe(true);
    expect(shouldSave(0, 6000, 5, 5.1)).toBe(false);
  });
});
