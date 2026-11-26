import { describe, expect, it } from "vitest";
import { canConvertToMp3, convertOutcome } from "./convert";

const ok = { bookStatus: "Liberated", isPodcastEpisode: false, absentFromLastScan: false, inTrash: false } as const;

describe("canConvertToMp3", () => {
  it("needs an m4b without an mp3", () => {
    expect(canConvertToMp3(ok, [{ name: "Book.M4B" }])).toBe(true);
    expect(canConvertToMp3(ok, [{ name: "Book.m4b" }, { name: "Book.mp3" }])).toBe(false);
    expect(canConvertToMp3(ok, [{ name: "Book.mp3" }])).toBe(false);
    expect(canConvertToMp3(ok, [])).toBe(false);
  });

  it("excludes unliberated, episodes, absent and trashed books", () => {
    const f = [{ name: "a.m4b" }];
    expect(canConvertToMp3({ ...ok, bookStatus: "NotLiberated" }, f)).toBe(false);
    expect(canConvertToMp3({ ...ok, isPodcastEpisode: true }, f)).toBe(false);
    expect(canConvertToMp3({ ...ok, absentFromLastScan: true }, f)).toBe(false);
    expect(canConvertToMp3({ ...ok, inTrash: true }, f)).toBe(false);
  });
});

describe("convertOutcome", () => {
  it("maps results", () => {
    expect(convertOutcome("ok")).toMatchObject({ bad: false, queue: true });
    expect(convertOutcome("conflict").bad).toBe(true);
    expect(convertOutcome("notFound").bad).toBe(true);
    expect(convertOutcome(undefined).bad).toBe(true);
  });
});
