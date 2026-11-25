import { api } from "./api/client";

let cached: Promise<string> | null = null;

/** Books folder from /api/settings, fetched once; empty string if unavailable. */
export function booksFolder(): Promise<string> {
  cached ??= api.settings().then(
    (s) => String(s.books ?? ""),
    () => {
      cached = null;
      return "";
    },
  );
  return cached;
}
