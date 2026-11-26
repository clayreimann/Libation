import { describe, expect, it } from "vitest";
import { confirmPhrase, phraseMatches, plusTargets, reduceRemoval, removalLine, REMOVE_MAX } from "./removeAudible";

describe("confirm phrase", () => {
  it("requires the exact text for the count", () => {
    expect(confirmPhrase(3)).toBe("remove 3 from audible");
    expect(phraseMatches("remove 3 from audible", 3)).toBe(true);
    expect(phraseMatches("remove 3 from audible", 2)).toBe(false);
    expect(phraseMatches("Remove 3 from audible", 3)).toBe(false);
    expect(phraseMatches("remove 3 from audible ", 3)).toBe(false);
    expect(phraseMatches("remove 0 from audible", 0)).toBe(false);
  });
});

describe("plusTargets", () => {
  it("keeps Plus books and counts the rest as excluded", () => {
    expect(plusTargets(["a", "b", "c"], new Set(["a", "c", "z"]))).toEqual({ targets: ["a", "c"], excluded: 1, overLimit: 0 });
  });

  it("caps at the API limit", () => {
    const sel = Array.from({ length: REMOVE_MAX + 5 }, (_, i) => `B${i}`);
    const t = plusTargets(sel, new Set(sel));
    expect(t.targets).toHaveLength(REMOVE_MAX);
    expect(t.overLimit).toBe(5);
    expect(t.excluded).toBe(0);
  });
});

describe("reduceRemoval", () => {
  it("buckets results and describes them", () => {
    const s = reduceRemoval({
      results: [
        { asin: "a", result: "removed" },
        { asin: "b", result: "failed", error: "boom" },
        { asin: "c", result: "failed" },
        { asin: "d", result: "notPlus" },
        { asin: "e", result: "notFound" },
      ],
    });
    expect(s.removed).toEqual(["a"]);
    expect(s.failed).toEqual([{ asin: "b", error: "boom" }, { asin: "c", error: "unknown error" }]);
    expect(removalLine(s)).toBe("1 removed from Audible, 2 failed, 1 not Audible Plus, 1 not found.");
  });
});
