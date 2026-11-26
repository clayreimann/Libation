import { describe, expect, it } from "vitest";
import { canRate, ratingError, setField, toMyRating } from "./rating";

describe("rating", () => {
  it("sets a field and clears it on a repeat click", () => {
    const r = { overall: 3, performance: 0, story: 4 };
    expect(setField(r, "performance", 5)).toEqual({ overall: 3, performance: 5, story: 4 });
    expect(setField(r, "story", 4)).toEqual({ overall: 3, performance: 0, story: 0 });
  });

  it("requires an overall", () => {
    expect(ratingError({ overall: 0, performance: 3, story: 3 })).not.toBeNull();
    expect(ratingError({ overall: 1, performance: 0, story: 0 })).toBeNull();
  });

  it("only rates products", () => {
    expect(canRate({ isPodcastEpisode: false, isPodcastParent: false })).toBe(true);
    expect(canRate({ isPodcastEpisode: true, isPodcastParent: false })).toBe(false);
    expect(canRate({ isPodcastEpisode: false, isPodcastParent: true })).toBe(false);
  });

  it("rounds the stored averages to whole stars", () => {
    expect(toMyRating({ overall: 4.4, performance: 3.6, story: 0 })).toEqual({ overall: 4, performance: 4, story: 0 });
  });
});
