import { describe, expect, it } from "vitest";
import { addTags, normalizeTags, removeTags } from "./tags";

describe("normalizeTags", () => {
  it("lowercases, maps hyphens, strips punctuation, dedupes and sorts", () => {
    expect(normalizeTags("  Zed  auto-bio, bio! bio\nZED ")).toEqual(["auto_bio", "bio", "zed"]);
  });
  it("is empty for blank input", () => {
    expect(normalizeTags("   ")).toEqual([]);
  });
});

describe("addTags / removeTags", () => {
  it("merges without duplicates", () => {
    expect(addTags("b a", "A c")).toBe("a b c");
  });
  it("adding nothing normalises the current tags", () => {
    expect(addTags("b  a", "")).toBe("a b");
  });
  it("removes case-insensitively and ignores absent tags", () => {
    expect(removeTags("a b c", "B x")).toBe("a c");
  });
  it("can remove everything", () => {
    expect(removeTags("a", "a")).toBe("");
  });
});
