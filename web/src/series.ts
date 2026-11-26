import type { SeriesGroup } from "./api/types";
import type { SortState } from "./sort";

export const seriesSort: SortState = { key: "series", dir: "asc" };

export const positionLabel = (order: string | null | undefined): string => (order ? `#${order}` : "");

export const progressLabel = (s: Pick<SeriesGroup, "bookCount" | "liberatedCount">): string => `${s.liberatedCount} of ${s.bookCount} liberated`;
