import { describe, expect, it } from "vitest";
import { bulkPushLine, clock, isNewerSkip, percentOf, progressRows, pushErrorText, pushView } from "./progress";
import type { BulkSummary } from "./bulk";

const summary = (o: Partial<BulkSummary>): BulkSummary => ({ total: 0, updated: 0, conflict: [], notFound: [], failed: [], cancelled: false, ...o });

describe("clock and percent", () => {
  it("formats positions", () => {
    expect(clock(65)).toBe("1:05");
    expect(clock(3725)).toBe("1:02:05");
    expect(clock(-3)).toBe("0:00");
  });

  it("clamps percent and handles unknown length", () => {
    expect(percentOf(1800, 60)).toBe(50);
    expect(percentOf(9999, 60)).toBe(100);
    expect(percentOf(10, 0)).toBeNull();
  });
});

describe("progressRows", () => {
  it("shows both sides", () => {
    const rows = progressRows({ audible: { positionMs: 1_800_000, lastUpdated: null }, audiobookshelf: { currentTimeSec: 600, isFinished: true, lastUpdate: null } }, 60);
    expect(rows[0]?.text).toMatch(/^30:00 \(50%\)/);
    expect(rows[1]?.text).toMatch(/^10:00 \(17%\), finished/);
  });

  it("explains missing sides", () => {
    const rows = progressRows({}, 60);
    expect(rows.map((r) => r.text)).toEqual(["no position", "not configured or no position"]);
  });
});

describe("pushView", () => {
  it("maps outcomes", () => {
    expect(pushView({ asin: "A", outcome: "pushed", positionSec: 90 })).toEqual({ kind: "ok", text: "Pushed 1:30 to Audiobookshelf." });
    expect(pushView({ asin: "A", outcome: "failed", reason: "boom" })).toEqual({ kind: "bad", text: "Push failed: boom" });
    expect(pushView({ asin: "A", outcome: "skipped", reason: "already in sync" }).kind).toBe("info");
  });

  it("detects a newer-server skip", () => {
    const r = { asin: "A", outcome: "skipped", reason: "Audiobookshelf progress is newer; skipped" };
    expect(isNewerSkip(r)).toBe(true);
    expect(pushView(r).kind).toBe("newer");
    expect(isNewerSkip({ ...r, outcome: "failed" })).toBe(false);
  });

  it("explains request errors", () => {
    expect(pushErrorText(409, "x")).toMatch(/Settings/);
    expect(pushErrorText(502, "bad gateway")).toBe("bad gateway");
  });
});

describe("bulkPushLine", () => {
  it("counts each bucket", () => {
    expect(bulkPushLine(summary({ updated: 3 }))).toBe("3 queued.");
    expect(bulkPushLine(summary({ updated: 1, conflict: ["a"], notFound: ["b"], failed: [{ asins: ["c", "d"], error: "e" }] }))).toBe(
      "1 queued, 1 not liberated or already queued, 1 unknown, 2 failed.",
    );
  });
});
