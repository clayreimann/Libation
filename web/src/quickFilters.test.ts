import { describe, expect, it } from "vitest";
import { addFilter, loadFilters, moveFilter, removeFilter, saveFilters } from "./quickFilters";

const memory = () => {
  const m = new Map<string, string>();
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) };
};

describe("quick filter storage", () => {
  it("round-trips", () => {
    const s = memory();
    saveFilters(["a", "b"], s);
    expect(loadFilters(s)).toEqual(["a", "b"]);
  });
  it("survives corrupt or missing data and a throwing store", () => {
    const s = memory();
    s.setItem("libation.quickFilters", "{nope");
    expect(loadFilters(s)).toEqual([]);
    s.setItem("libation.quickFilters", JSON.stringify(["x", 3, " "]));
    expect(loadFilters(s)).toEqual(["x"]);
    const boom = { getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); } };
    expect(loadFilters(boom)).toEqual([]);
    expect(() => saveFilters(["a"], boom)).not.toThrow();
    expect(loadFilters(undefined)).toEqual([]);
  });
});

describe("quick filter list ops", () => {
  it("adds trimmed, ignores blanks and duplicates", () => {
    expect(addFilter(["a"], " b ")).toEqual(["a", "b"]);
    expect(addFilter(["a"], "a")).toEqual(["a"]);
    expect(addFilter(["a"], "  ")).toEqual(["a"]);
  });
  it("removes by index", () => {
    expect(removeFilter(["a", "b", "c"], 1)).toEqual(["a", "c"]);
  });
  it("moves within bounds only", () => {
    expect(moveFilter(["a", "b", "c"], 1, -1)).toEqual(["b", "a", "c"]);
    expect(moveFilter(["a", "b", "c"], 2, 1)).toEqual(["a", "b", "c"]);
    expect(moveFilter(["a", "b", "c"], 0, -1)).toEqual(["a", "b", "c"]);
  });
});
