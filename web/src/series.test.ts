import { describe, expect, it } from "vitest";
import { positionLabel, progressLabel, seriesSort } from "./series";

describe("series", () => {
  it("labels positions", () => {
    expect(positionLabel("2")).toBe("#2");
    expect(positionLabel(null)).toBe("");
  });
  it("labels progress", () => {
    expect(progressLabel({ bookCount: 3, liberatedCount: 1 })).toBe("1 of 3 liberated");
  });
  it("sorts by series ascending", () => {
    expect(seriesSort).toEqual({ key: "series", dir: "asc" });
  });
});
