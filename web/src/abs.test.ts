import { describe, expect, it } from "vitest";
import { defaultFolder, describeLibrariesError, describeTest, foldersOf, testRequest } from "./abs";
import type { AbsLibrary } from "./api/types";

const libs: AbsLibrary[] = [
  { id: "l1", name: "Books", mediaType: "book", folders: [{ id: "f1", path: "/a" }, { id: "f2", path: "/b" }] },
  { id: "l2", name: "Empty", mediaType: "book", folders: [] },
];

describe("describeTest", () => {
  it("summarises a success", () => {
    expect(describeTest({ ok: true, serverVersion: "2.8.0", user: "root" })).toEqual({ ok: true, text: "Connected, server 2.8.0, as root." });
  });

  it("explains each failure code and keeps the server's detail", () => {
    expect(describeTest({ ok: false, code: "unreachable", error: "Connection refused" }).text).toMatch(/could not be reached.*Connection refused/);
    expect(describeTest({ ok: false, code: "unauthorized", error: null }).text).toBe("The server rejected the token.");
    expect(describeTest({ ok: false, code: "weird", error: "odd" }).text).toBe("odd");
  });
});

describe("testRequest", () => {
  it("sends the URL only when edited and the token only when typed", () => {
    expect(testRequest("http://a", "http://a", "")).toEqual({});
    expect(testRequest("http://a", "http://b", "t")).toEqual({ url: "http://b", token: "t" });
    expect(testRequest("", "", " ")).toEqual({});
  });
});

describe("libraries", () => {
  it("lists folders and picks a default", () => {
    expect(foldersOf(libs, "l1")).toHaveLength(2);
    expect(foldersOf(libs, "nope")).toEqual([]);
    expect(defaultFolder(libs, "l1")).toBe("f1");
    expect(defaultFolder(libs, "l2")).toBe("");
  });

  it("describes listing failures", () => {
    expect(describeLibrariesError(409, "x")).toMatch(/Save a server URL/);
    expect(describeLibrariesError(502, "Connection refused", "unreachable")).toMatch(/could not be reached.*Connection refused/);
    expect(describeLibrariesError(500, "boom")).toBe("boom");
  });
});
