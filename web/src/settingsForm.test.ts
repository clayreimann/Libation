import { describe, expect, it } from "vitest";
import { booksFixedByEnvironment, buildPatch, isDirty, toDraft } from "./settingsForm";
import type { Settings } from "./api/types";

const settings = {
  books: "/data",
  inProgress: "/tmp/x",
  naming: { folderTemplate: "<title>", fileTemplate: "<title> [<id>]", chapterFileTemplate: "c", chapterTitleTemplate: "t" },
  download: { quality: "High", createCueSheet: false, allowLibationFixup: true, minimumFileDuration: 3 },
  mp3: { bitrate: 64, encoderQuality: "High" },
  queue: { dailyDownloadLimit: "NoLimit" },
  library: { importEpisodes: true },
  logLevel: "Information",
} as unknown as Settings;

describe("buildPatch", () => {
  const saved = toDraft(settings);

  it("is empty when nothing changed", () => {
    const b = buildPatch(saved, { ...saved });
    expect(b.changed).toEqual([]);
    expect(b.patch).toEqual({});
    expect(isDirty(saved, saved)).toBe(false);
  });

  it("nests only the changed keys and types numbers and booleans", () => {
    const b = buildPatch(saved, { ...saved, "download.createCueSheet": true, "mp3.bitrate": "96", "naming.fileTemplate": " <title> ", logLevel: "Debug" });
    expect(b.patch).toEqual({
      download: { createCueSheet: true },
      mp3: { bitrate: 96 },
      naming: { fileTemplate: "<title>" },
      logLevel: "Debug",
    });
  });

  it("sends null to reset the working folder", () => {
    expect(buildPatch(saved, { ...saved, inProgress: "  " }).patch).toEqual({ inProgress: null });
  });

  it("rejects a non-integer number locally and leaves it out", () => {
    const b = buildPatch(saved, { ...saved, "mp3.bitrate": "fast" });
    expect(b.errors["mp3.bitrate"]).toMatch(/whole number/);
    expect(b.patch).toEqual({});
    expect(isDirty(saved, { ...saved, "mp3.bitrate": "fast" })).toBe(true);
  });

  it("skips locked keys", () => {
    expect(buildPatch(saved, { ...saved, books: "/other" }, new Set(["books"])).patch).toEqual({});
  });
});

describe("booksFixedByEnvironment", () => {
  it("recognises the daemon's message", () => {
    expect(booksFixedByEnvironment({ books: "Set by the LIBATION_BOOKS_DIR environment variable; change that instead." })).toBe(true);
    expect(booksFixedByEnvironment({ books: "Must be an absolute path." })).toBe(false);
    expect(booksFixedByEnvironment(undefined)).toBe(false);
  });
});
