import { describe, expect, it } from "vitest";
import type { Book } from "./api/types";
import { groupPodcasts, needsParents } from "./podcasts";

const book = (asin: string, parentAsin: string | null = null): Book => ({ asin, parentAsin }) as Book;

describe("groupPodcasts", () => {
  const parents = [book("P1"), book("P2")];

  it("nests episodes under one parent row at the first episode's position", () => {
    const rows = groupPodcasts([book("A"), book("E1", "P1"), book("B"), book("E2", "P1")], parents);
    expect(rows.map((r) => (r.kind === "book" ? r.book.asin : `${r.parent.asin}:${r.episodes.map((e) => e.asin)}`))).toEqual(["A", "P1:E1,E2", "B"]);
  });

  it("keeps separate groups per parent", () => {
    const rows = groupPodcasts([book("E1", "P1"), book("E3", "P2"), book("E2", "P1")], parents);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ kind: "parent", parent: { asin: "P1" }, episodes: [{ asin: "E1" }, { asin: "E2" }] });
  });

  it("leaves an episode with an unlisted parent standalone", () => {
    expect(groupPodcasts([book("E9", "GONE")], parents)).toEqual([{ kind: "book", book: book("E9", "GONE") }]);
  });

  it("is a no-op without parents", () => {
    expect(groupPodcasts([book("A"), book("B")], [])).toHaveLength(2);
  });
});

describe("needsParents", () => {
  it("is true only when some item is an episode", () => {
    expect(needsParents([book("A"), book("E", "P")])).toBe(true);
    expect(needsParents([book("A")])).toBe(false);
  });
});
