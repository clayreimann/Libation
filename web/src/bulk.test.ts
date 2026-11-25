import { describe, expect, it } from "vitest";
import { notOk, runBulk, runChunked } from "./bulk";

const tick = () => new Promise((r) => setTimeout(r, 1));

describe("runBulk", () => {
  it("never exceeds the concurrency limit and visits every item", async () => {
    let active = 0;
    let peak = 0;
    const seen: number[] = [];
    const progress: number[] = [];
    const items = Array.from({ length: 50 }, (_, i) => i);
    const res = await runBulk(items, {
      concurrency: 4,
      onProgress: (d) => progress.push(d),
      worker: async (i) => {
        peak = Math.max(peak, ++active);
        await tick();
        seen.push(i);
        active--;
      },
    });
    expect(peak).toBe(4);
    expect(res).toEqual({ ok: 50, failed: [], cancelled: false });
    expect(seen.sort((a, b) => a - b)).toEqual(items);
    expect(progress.at(-1)).toBe(50);
  });

  it("collects failures and keeps going", async () => {
    const res = await runBulk(["a", "b", "c"], {
      worker: async (s) => {
        if (s === "b") throw new Error("boom");
      },
    });
    expect(res.ok).toBe(2);
    expect(res.failed).toEqual([{ item: "b", error: "boom" }]);
  });

  it("stops starting new items once aborted", async () => {
    const ctl = new AbortController();
    const res = await runBulk([1, 2, 3, 4, 5, 6], {
      concurrency: 1,
      signal: ctl.signal,
      worker: async (n) => {
        if (n === 2) ctl.abort();
      },
    });
    expect(res.cancelled).toBe(true);
    expect(res.ok).toBe(2);
  });
});

describe("runChunked", () => {
  const ok = async (chunk: string[]) => ({ updated: chunk.length, missing: [], results: chunk.map((asin) => ({ asin, result: "ok" })) });

  it("splits into chunks and sums updated", async () => {
    const sizes: number[] = [];
    const s = await runChunked(["a", "b", "c", "d", "e"], async (c) => (sizes.push(c.length), ok(c)), { chunkSize: 2 });
    expect(sizes).toEqual([2, 2, 1]);
    expect(s).toMatchObject({ total: 5, updated: 5, conflict: [], notFound: [], failed: [], cancelled: false });
  });

  it("collects conflicts, missing and failed chunks", async () => {
    let n = 0;
    const s = await runChunked(["a", "b", "c", "d"], async () => {
      if (n++ === 1) throw new Error("boom");
      return { updated: 0, missing: ["b"], results: [{ asin: "a", result: "conflict" }, { asin: "b", result: "notFound" }] };
    }, { chunkSize: 2 });
    expect(s.conflict).toEqual(["a"]);
    expect(s.notFound).toEqual(["b"]);
    expect(s.failed).toEqual([{ asins: ["c", "d"], error: "boom" }]);
    expect(notOk(s).sort()).toEqual(["a", "b", "c", "d"]);
  });

  it("stops between chunks when aborted", async () => {
    const ctl = new AbortController();
    const s = await runChunked(["a", "b", "c"], async (c) => (ctl.abort(), ok(c)), { chunkSize: 1, signal: ctl.signal });
    expect(s.cancelled).toBe(true);
    expect(s.updated).toBe(1);
  });
});
