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
  scan: () => request<Status>("POST", "/api/scan"),
  liberate: () => request<Status>("POST", "/api/liberate"),
  decide: (id: number, body: DecisionRequest) => request<Queue>("POST", `/api/queue/decisions/${id}`, body),
};
