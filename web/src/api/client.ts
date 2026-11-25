import type { SortState } from "../sort";
import { runChunked, type BulkSummary, type ChunkOptions } from "../bulk";
import type { Account, AsinList, BulkResult, Book, BookDetail, DecisionRequest, LibraryPage, Queue, Settings, Stats, Status } from "./types";

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method,
    signal,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let json: unknown;
  try {
    json = text ? JSON.parse(text) : undefined;
  } catch {
    json = undefined;
  }
  if (!res.ok) {
    const message = (json as { error?: string } | undefined)?.error ?? (res.status === 401 ? "Unauthorized" : `${res.status} ${res.statusText}`);
    throw new ApiError(res.status, message);
  }
  return json as T;
}

export type LiberatedChoice = "liberated" | "notLiberated";
export type PdfChoice = LiberatedChoice;
type BulkOpts = Omit<ChunkOptions, "chunkSize">;

const book = (asin: string) => `/api/library/${encodeURIComponent(asin)}`;

export const api = {
  status: (signal?: AbortSignal) => request<Status>("GET", "/api/status", undefined, signal),
  accounts: () => request<Account[]>("GET", "/api/accounts"),
  settings: () => request<Settings>("GET", "/api/settings"),
  queue: (signal?: AbortSignal) => request<Queue>("GET", "/api/queue", undefined, signal),
  library: (params: { offset: number; limit: number; q: string; trash: boolean; sort?: SortState | null }, signal?: AbortSignal) => {
    const qs = new URLSearchParams({ offset: String(params.offset), limit: String(params.limit) });
    if (params.q.trim()) qs.set("q", params.q.trim());
    if (params.trash) qs.set("trash", "true");
    if (params.sort) {
      qs.set("sort", params.sort.key);
      qs.set("dir", params.sort.dir);
    }
    return request<LibraryPage>("GET", `/api/library?${qs}`, undefined, signal);
  },
  asins: (params: { q: string; trash: boolean }, signal?: AbortSignal) => {
    const qs = new URLSearchParams();
    if (params.q.trim()) qs.set("q", params.q.trim());
    if (params.trash) qs.set("trash", "true");
    return request<AsinList>("GET", `/api/library/asins?${qs}`, undefined, signal);
  },
  stats: (signal?: AbortSignal) => request<Stats>("GET", "/api/library/stats", undefined, signal),
  coverUrl: (asin: string, size: "small" | "large") => `${book(asin)}/cover?size=${size}`,
  trash: (asin: string) => request<Book>("POST", `${book(asin)}/trash`),
  restore: (asin: string) => request<Book>("POST", `${book(asin)}/restore`),
  liberateBook: (asin: string) => request<unknown>("POST", `${book(asin)}/liberate`),
  getBook: (asin: string, signal?: AbortSignal) => request<BookDetail>("GET", book(asin), undefined, signal),
  setTags: (asin: string, tags: string) => request<Book>("PUT", `${book(asin)}/tags`, { tags }),
  setStatus: (asin: string, status: LiberatedChoice) => request<Book>("PUT", `${book(asin)}/status`, { status }),
  scan: () => request<Status>("POST", "/api/scan"),
  liberate: () => request<Status>("POST", "/api/liberate"),
  decide: (id: number, body: DecisionRequest) => request<Queue>("POST", `/api/queue/decisions/${id}`, body),

  // One function per operation; each chunks at the API's 5000 ASIN limit.
  bulkTrash: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/trash", asins, {}, o),
  bulkRestore: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/restore", asins, {}, o),
  bulkPurge: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/purge", asins, {}, o),
  bulkLiberate: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/liberate", asins, {}, o),
  bulkSetStatus: (asins: string[], status: LiberatedChoice, o?: BulkOpts) => chunked("PUT", "/api/library/status", asins, { status }, o),
  bulkSetPdfStatus: (asins: string[], status: PdfChoice, o?: BulkOpts) => chunked("PUT", "/api/library/pdf-status", asins, { status }, o),
  bulkAddTags: (asins: string[], tags: string, o?: BulkOpts) => chunked("PUT", "/api/library/tags", asins, { add: tags }, o),
  bulkRemoveTags: (asins: string[], tags: string, o?: BulkOpts) => chunked("PUT", "/api/library/tags", asins, { remove: tags }, o),
};

function chunked(method: string, path: string, asins: string[], extra: object, o?: BulkOpts): Promise<BulkSummary> {
  return runChunked(asins, (chunk) => request<BulkResult>(method, path, { asins: chunk, ...extra }, o?.signal), o);
}
