import { describe, expect, it } from "vitest";
import type { QualityResult, QualityScan } from "./api/types";
import { applyLine, bitrateGain, isScanning, qualityLabel, scanLine, toggleAll } from "./quality";

const r = (asin: string, cur = 64, avail = 128): QualityResult => ({ asin, title: asin, currentQuality: { codec: "AAC-LC", bitrate: cur }, availableQuality: { codec: "xHE-AAC", bitrate: avail } });
const scan = (o: Partial<QualityScan>): QualityScan => ({ state: "idle", widevine: false, total: 0, scanned: 0, failed: 0, ...o });

describe("quality", () => {
  it("labels and compares", () => {
    expect(qualityLabel({ codec: "AAC-LC", bitrate: 64 })).toBe("AAC-LC 64 kbps");
    expect(bitrateGain(r("a"))).toBe(100);
    expect(bitrateGain(r("a", 0))).toBeNull();
  });

  it("describes scans", () => {
    expect(scanLine(scan({ state: "running", total: 10, scanned: 3 }))).toBe("Scanning: 3 of 10 scanned.");
    expect(scanLine(scan({ state: "completed", total: 2, scanned: 2, failed: 1, results: [r("a")] }))).toBe("Finished: 2 of 2 scanned, 1 failed, 1 with better quality available.");
    expect(scanLine(scan({ state: "failed", error: "boom" }))).toBe("Failed: boom");
    expect(isScanning(scan({ state: "cancelling" }))).toBe(true);
    expect(isScanning(scan({ state: "completed" }))).toBe(false);
  });

  it("describes an apply", () => {
    expect(applyLine({ marked: 2, missing: ["x"], widevineEnabled: true, xHeAacRequested: false, accountsMarkedForReRegistration: true })).toBe(
      "2 marked as not downloaded, 1 not in the last results, Widevine turned on, accounts need signing in again before downloading.",
    );
  });

  it("toggles all", () => {
    const rs = [r("a"), r("b")];
    expect([...toggleAll(new Set(["a"]), rs)]).toEqual(["a", "b"]);
    expect(toggleAll(new Set(["a", "b"]), rs).size).toBe(0);
  });
});
