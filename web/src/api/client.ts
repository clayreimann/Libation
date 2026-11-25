import { runBulk, SKIPPED, type BulkOptions, type BulkResult } from "../bulk";
import { addTags, removeTags } from "../tags";
import type { Account, Book, DecisionRequest, LibraryPage, Queue, Settings, Status } from "./types";

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
type BulkOpts = Omit<BulkOptions<string>, "worker">;

const book = (asin: string) => `/api/library/${encodeURIComponent(asin)}`;

export const api = {
  status: (signal?: AbortSignal) => request<Status>("GET", "/api/status", undefined, signal),
  accounts: () => request<Account[]>("GET", "/api/accounts"),
  settings: () => request<Settings>("GET", "/api/settings"),
  queue: (signal?: AbortSignal) => request<Queue>("GET", "/api/queue", undefined, signal),
  library: (params: { offset: number; limit: number; q: string; trash: boolean }, signal?: AbortSignal) => {
    const qs = new URLSearchParams({ offset: String(params.offset), limit: String(params.limit) });
    if (params.q.trim()) qs.set("q", params.q.trim());
    if (params.trash) qs.set("trash", "true");
    return request<LibraryPage>("GET", `/api/library?${qs}`, undefined, signal);
  },
  trash: (asin: string) => request<Book>("POST", `${book(asin)}/trash`),
  restore: (asin: string) => request<Book>("POST", `${book(asin)}/restore`),
  liberateBook: (asin: string) => request<unknown>("POST", `${book(asin)}/liberate`),
  getBook: (asin: string) => request<Book>("GET", book(asin)),
  setTags: (asin: string, tags: string) => request<Book>("PUT", `${book(asin)}/tags`, { tags }),
  setStatus: (asin: string, status: LiberatedChoice) => request<Book>("PUT", `${book(asin)}/status`, { status }),
  scan: () => request<Status>("POST", "/api/scan"),
  liberate: () => request<Status>("POST", "/api/liberate"),
  decide: (id: number, body: DecisionRequest) => request<Queue>("POST", `/api/queue/decisions/${id}`, body),

  // One function per operation: swap the body for a bulk endpoint when the API grows one.
  bulkTrash: (asins: string[], o?: BulkOpts): Promise<BulkResult<string>> => runBulk(asins, { ...o, worker: (a) => api.trash(a) }),
  bulkRestore: (asins: string[], o?: BulkOpts): Promise<BulkResult<string>> => runBulk(asins, { ...o, worker: (a) => api.restore(a) }),
  bulkLiberate: (asins: string[], o?: BulkOpts): Promise<BulkResult<string>> =>
    runBulk(asins, {
      ...o,
      worker: async (a) => {
        try {
          await api.liberateBook(a);
        } catch (e) {
          if (e instanceof ApiError && e.status === 409) return SKIPPED;
          throw e;
        }
      },
    }),
  bulkSetStatus: (asins: string[], status: LiberatedChoice, o?: BulkOpts): Promise<BulkResult<string>> =>
    runBulk(asins, { ...o, worker: (a) => api.setStatus(a, status) }),
  bulkAddTags: (asins: string[], tags: string, o?: BulkOpts): Promise<BulkResult<string>> => bulkEditTags(asins, (cur) => addTags(cur, tags), o),
  bulkRemoveTags: (asins: string[], tags: string, o?: BulkOpts): Promise<BulkResult<string>> =>
    bulkEditTags(asins, (cur) => removeTags(cur, tags), o),
};

function bulkEditTags(asins: string[], edit: (current: string) => string, o?: BulkOpts): Promise<BulkResult<string>> {
  return runBulk(asins, {
    ...o,
    worker: async (a) => {
      const current = (await api.getBook(a)).tags;
      const next = edit(current);
      if (next === current) return SKIPPED;
      await api.setTags(a, next);
    },
  });
}
