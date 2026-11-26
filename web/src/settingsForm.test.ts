import { describe, expect, it } from "vitest";
import { booksFixedByEnvironment, buildPatch, isDirty, toDraft, withTokenClear } from "./settingsForm";
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

describe("audiobookshelf fields", () => {
  const abs = { ...settings, audiobookshelf: { enabled: false, serverUrl: "http://a", libraryId: "l", folderId: "f", tokenSet: true } } as unknown as Settings;
  const saved = toDraft(abs);

  it("never puts a token in the draft and sends one only when typed", () => {
    expect(saved["audiobookshelf.apiToken"]).toBe("");
    expect(buildPatch(saved, { ...saved }).changed).toEqual([]);
    expect(buildPatch(saved, { ...saved, "audiobookshelf.apiToken": " tok " }).patch).toEqual({ audiobookshelf: { apiToken: "tok" } });
  });

  it("clears url, library and folder with null", () => {
    const b = buildPatch(saved, { ...saved, "audiobookshelf.serverUrl": "", "audiobookshelf.libraryId": "", "audiobookshelf.folderId": "" });
    expect(b.patch).toEqual({ audiobookshelf: { serverUrl: null, libraryId: null, folderId: null } });
  });

  it("adds a null token for a clear unless a new one was typed", () => {
    expect(withTokenClear(buildPatch(saved, saved), true).patch).toEqual({ audiobookshelf: { apiToken: null } });
    const typed = buildPatch(saved, { ...saved, "audiobookshelf.apiToken": "n" });
    expect(withTokenClear(typed, true)).toBe(typed);
    const plain = buildPatch(saved, saved);
    expect(withTokenClear(plain, false)).toBe(plain);
  });
});

describe("booksFixedByEnvironment", () => {
  it("recognises the daemon's message", () => {
    expect(booksFixedByEnvironment({ books: "Set by the LIBATION_BOOKS_DIR environment variable; change that instead." })).toBe(true);
    expect(booksFixedByEnvironment({ books: "Must be an absolute path." })).toBe(false);
    expect(booksFixedByEnvironment(undefined)).toBe(false);
  });
});
