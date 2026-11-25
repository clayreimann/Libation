import { describe, expect, it } from "vitest";
import { runBulk } from "./bulk";

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
