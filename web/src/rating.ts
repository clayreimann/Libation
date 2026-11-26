import type { Book } from "./api/types";

export interface MyRating {
  overall: number;
  performance: number;
  story: number;
}
export type RatingField = keyof MyRating;

export const ratingFields: { key: RatingField; label: string }[] = [
  { key: "overall", label: "Overall" },
  { key: "performance", label: "Performance" },
  { key: "story", label: "Story" },
];

/** Audible rates products only, so podcast episodes and parents are out. */
export const canRate = (b: Pick<Book, "isPodcastEpisode" | "isPodcastParent">): boolean => !b.isPodcastEpisode && !b.isPodcastParent;

/** Clicking the star already set clears it. */
export function setField(r: MyRating, key: RatingField, stars: number): MyRating {
  return { ...r, [key]: r[key] === stars ? 0 : stars };
}

/** The daemon ignores an overall of 0, so it cannot be cleared or saved without one. */
export const ratingError = (r: MyRating): string | null => (r.overall === 0 ? "Set an overall rating first; Audible ignores a rating without one." : null);

export const toMyRating = (r: MyRating): MyRating => ({ overall: Math.round(r.overall), performance: Math.round(r.performance), story: Math.round(r.story) });
