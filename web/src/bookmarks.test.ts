import { describe, expect, it } from "vitest";
import { bookmarkLine, clock, countLine } from "./bookmarks";

describe("bookmarks", () => {
  it("formats clock times", () => {
    expect(clock(5.9)).toBe("0:05");
    expect(clock(125)).toBe("2:05");
    expect(clock(3725)).toBe("1:02:05");
  });
  it("describes records", () => {
    expect(bookmarkLine({ type: "Clip", start: 10.5, end: 20 })).toBe("Clip 0:10 to 0:20");
    expect(bookmarkLine({ type: "Bookmark", start: 61, end: null })).toBe("Bookmark at 1:01");
  });
  it("counts by type", () => {
    expect(countLine([])).toBe("No clips, notes or bookmarks.");
    expect(countLine([{ type: "Clip" }, { type: "Clip" }, { type: "Note" }])).toBe("2 clips, 1 note");
  });
});
