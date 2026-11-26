import { countQueries } from "./filters";

/** Sidebar scopes, the web counterpart of the desktop app's LibraryScope. */
export const scopes = [
  { id: "all", label: "All books", group: "library", query: "" },
  { id: "not-downloaded", label: "Not downloaded", group: "library", query: countQueries.notLiberated },
  { id: "downloaded", label: "Downloaded", group: "library", query: countQueries.liberated },
  { id: "needs-attention", label: "Needs attention", group: "library", query: countQueries.error },
  { id: "unavailable", label: "Unavailable", group: "library", query: countQueries.unavailable },
  { id: "series", label: "Series", group: "browse", query: "IsInSeries" },
  { id: "podcasts", label: "Podcasts", group: "browse", query: "Episode" },
  { id: "plus", label: "Audible Plus", group: "browse", query: "IsAudiblePlus" },
  { id: "trash", label: "Trash", group: "other", query: "" },
] as const;

export type Scope = (typeof scopes)[number]["id"];
export type Page = "library" | "queue" | "accounts" | "settings";
export interface Route {
  page: Page;
  scope: Scope;
}

const pages: readonly Page[] = ["library", "queue", "accounts", "settings"];

export function parseRoute(hash: string): Route {
  const [first, second] = hash.replace(/^#\/?/, "").split("/");
  const page = pages.find((p) => p === first) ?? "library";
  const scope = scopes.find((s) => s.id === second)?.id ?? "all";
  return { page, scope: page === "library" ? scope : "all" };
}

export function routeHref(r: Partial<Route>): string {
  const page = r.page ?? "library";
  if (page !== "library") return `#/${page}`;
  return !r.scope || r.scope === "all" ? "#/library" : `#/library/${r.scope}`;
}

export const scopeLabel = (s: Scope): string => scopes.find((x) => x.id === s)!.label;

/** The scope's query ANDed with what the user typed; either may be empty. */
export function scopedQuery(scope: Scope, typed: string): string {
  const base = scopes.find((s) => s.id === scope)!.query;
  const t = typed.trim();
  if (!base) return t;
  return t ? `(${base}) AND (${t})` : base;
}
