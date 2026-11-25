import { describe, expect, it } from "vitest";
import { parseSort, toggleSort } from "./sort";
import { defaultVisible, parseVisible, toggleColumn } from "./columns";
import { readPref, writePref } from "./prefs";

describe("sort", () => {
  it("starts ascending, then flips on the same column", () => {
    const a = toggleSort(null, "title");
    expect(a).toEqual({ key: "title", dir: "asc" });
    expect(toggleSort(a, "title")).toEqual({ key: "title", dir: "desc" });
    expect(toggleSort({ key: "title", dir: "desc" }, "title")).toEqual({ key: "title", dir: "asc" });
  });

  it("restarts ascending on a different column", () => {
    expect(toggleSort({ key: "title", dir: "desc" }, "length")).toEqual({ key: "length", dir: "asc" });
  });

  it("rejects persisted garbage", () => {
    expect(parseSort({ key: "nope", dir: "asc" })).toBeNull();
    expect(parseSort({ key: "title", dir: "up" })).toBeNull();
    expect(parseSort("title")).toBeNull();
    expect(parseSort({ key: "rating", dir: "desc" })).toEqual({ key: "rating", dir: "desc" });
  });
});

describe("columns", () => {
  it("falls back to defaults when nothing valid is stored", () => {
    expect(parseVisible(undefined)).toEqual(defaultVisible());
    expect(parseVisible("x")).toEqual(defaultVisible());
  });

  it("drops unknown ids and keeps display order", () => {
    expect(parseVisible(["tags", "bogus", "authors"])).toEqual(["authors", "tags"]);
  });

  it("toggles on and off", () => {
    const on = toggleColumn(["authors"], "rating");
    expect(on).toEqual(["authors", "rating"]);
    expect(toggleColumn(on, "authors")).toEqual(["rating"]);
  });
});

describe("prefs", () => {
  it("survives a throwing store", () => {
    const bad = { getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); } };
    expect(readPref("k", bad)).toBeUndefined();
    expect(() => writePref("k", 1, bad)).not.toThrow();
  });

  it("round-trips", () => {
    const m = new Map<string, string>();
    const store = { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) };
    writePref("k", { a: 1 }, store);
    expect(readPref("k", store)).toEqual({ a: 1 });
  });
});
