import { describe, expect, it } from "vitest";
import { normalizeTags } from "./tags";

describe("normalizeTags", () => {
  it("lowercases, maps hyphens, strips punctuation, dedupes and sorts", () => {
    expect(normalizeTags("  Zed  auto-bio, bio! bio\nZED ")).toEqual(["auto_bio", "bio", "zed"]);
  });
  it("is empty for blank input", () => {
    expect(normalizeTags("   ")).toEqual([]);
  });
});
