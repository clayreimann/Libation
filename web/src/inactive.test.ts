import { describe, expect, it } from "vitest";
import { inactiveLine, remainingResults, toggleAllInactive } from "./inactive";
import type { InactiveScan } from "./api/types";

const book = (asin: string, title: string) => ({ asin, title, authors: [], account: "me" });
const scan = (o: Partial<InactiveScan>): InactiveScan => ({ state: "completed", total: 5, ...o });

describe("inactiveLine", () => {
  it("describes each state", () => {
    expect(inactiveLine(scan({ state: "running", scanned: 2 }))).toBe("Scanning: 2 of 5 checked.");
    expect(inactiveLine(scan({ state: "running" }))).toBe("Scanning: 5 books to compare.");
    expect(inactiveLine(scan({ results: [book("a", "A")] }))).toMatch(/1 book is no longer/);
    expect(inactiveLine(scan({ results: [] }))).toMatch(/still in the Audible account/);
    expect(inactiveLine(scan({ state: "failed", error: "needs login" }))).toBe("Failed: needs login");
  });
});

describe("results", () => {
  it("drops trashed books and sorts by title", () => {
    const s = scan({ results: [book("b", "Beta"), book("a", "Alpha"), book("c", "Gamma")] });
    expect(remainingResults(s, new Set(["c"])).map((r) => r.asin)).toEqual(["a", "b"]);
    expect(remainingResults(null, new Set())).toEqual([]);
  });

  it("toggles all", () => {
    const r = [book("a", "A"), book("b", "B")];
    expect(toggleAllInactive(new Set(), r)).toEqual(new Set(["a", "b"]));
    expect(toggleAllInactive(new Set(["a", "b"]), r)).toEqual(new Set());
  });
});
