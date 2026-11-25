import { describe, expect, it } from "vitest";
import { addFilter, clearLegacyFilters, defaultQuery, label, loadLegacyFilters, migrationOffer, moveFilter, parseFilterError, removeFilter, toNamed, updateFilter } from "./quickFilters";

const memory = (init?: string) => {
  const m = new Map<string, string>();
  if (init !== undefined) m.set("libation.quickFilters", init);
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v), removeItem: (k: string) => void m.delete(k) };
};

describe("legacy localStorage filters", () => {
  it("reads and clears", () => {
    const s = memory(JSON.stringify(["a", "b"]));
    expect(loadLegacyFilters(s)).toEqual(["a", "b"]);
    clearLegacyFilters(s);
    expect(loadLegacyFilters(s)).toEqual([]);
  });
  it("survives corrupt data and a throwing store", () => {
    expect(loadLegacyFilters(memory("{nope"))).toEqual([]);
    expect(loadLegacyFilters(memory(JSON.stringify(["x", 3, " "])))).toEqual(["x"]);
    const boom = { getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); }, removeItem: () => { throw new Error("denied"); } };
    expect(loadLegacyFilters(boom)).toEqual([]);
    expect(() => clearLegacyFilters(boom)).not.toThrow();
    expect(loadLegacyFilters(undefined)).toEqual([]);
  });
});

describe("migration offer", () => {
  it("offers browser filters only when the server list is empty", () => {
    expect(migrationOffer([], ["a", "b", "a"])).toEqual(["a", "b"]);
    expect(migrationOffer([{ filter: "x" }], ["a"])).toEqual([]);
    expect(migrationOffer([], [])).toEqual([]);
  });
  it("converts to named filters", () => {
    expect(toNamed(["a"])).toEqual([{ filter: "a" }]);
  });
});

describe("default and labels", () => {
  const filters = [{ filter: "tag:skip", name: "Skipped" }, { filter: "author:x" }];
  it("applies the first filter only when useDefault", () => {
    expect(defaultQuery({ useDefault: true, filters })).toBe("tag:skip");
    expect(defaultQuery({ useDefault: false, filters })).toBe("");
    expect(defaultQuery({ useDefault: true, filters: [] })).toBe("");
    expect(defaultQuery(null)).toBe("");
  });
  it("labels by name, falling back to the filter", () => {
    expect(label(filters[0]!)).toBe("Skipped");
    expect(label(filters[1]!)).toBe("author:x");
    expect(label({ filter: "f", name: "  " })).toBe("f");
  });
});

describe("list ops", () => {
  const a = { filter: "a" };
  const b = { filter: "b" };
  const c = { filter: "c" };
  it("adds trimmed, ignores blanks and duplicates", () => {
    expect(addFilter([a], " b ")).toEqual([a, b]);
    expect(addFilter([a], "a")).toEqual([a]);
    expect(addFilter([a], "  ")).toEqual([a]);
  });
  it("removes and renames by index", () => {
    expect(removeFilter([a, b, c], 1)).toEqual([a, c]);
    expect(updateFilter([a, b], 1, { name: "B" })).toEqual([a, { filter: "b", name: "B" }]);
  });
  it("moves within bounds only", () => {
    expect(moveFilter([a, b, c], 1, -1)).toEqual([b, a, c]);
    expect(moveFilter([a, b, c], 2, 1)).toEqual([a, b, c]);
    expect(moveFilter([a, b, c], 0, -1)).toEqual([a, b, c]);
  });
});

describe("parseFilterError", () => {
  it("extracts the index and message", () => {
    expect(parseFilterError("filters[1]: Cannot parse 'x'")).toEqual({ index: 1, message: "Cannot parse 'x'" });
    expect(parseFilterError("filters is required.")).toBeNull();
  });
});
