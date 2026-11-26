import type { SortState } from "../sort";
import { runChunked, type BulkSummary, type ChunkOptions } from "../bulk";
import type { Account, AccountPatch, AbsLibrary, AbsTest, AbsTestRequest, AsinList, BulkResult, Book, BookDetail, CancelJob, DecisionRequest, FinishedJob, JobLog, LibraryPage, LoginStart, LoginStartRequest, MoveTo, QuickFilters, Queue, LocateScan, Settings, SettingsPatch, Stats, Status, TemplatePreview, TemplatePreviewRequest } from "./types";

export interface ApiErrorDetail {
  code?: string | null;
  retryable?: boolean | null;
  fields?: Record<string, string>;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly detail: ApiErrorDetail = {},
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
    const body = json as ({ error?: string } & ApiErrorDetail) | undefined;
    const message = body?.error ?? (res.status === 401 ? "Unauthorized" : `${res.status} ${res.statusText}`);
    throw new ApiError(res.status, message, { code: body?.code, retryable: body?.retryable, fields: body?.fields });
  }
  return json as T;
}

export type ExportFormat = "csv" | "xlsx" | "json";
export type LiberatedChoice = "liberated" | "notLiberated";
export type PdfChoice = LiberatedChoice;
type BulkOpts = Omit<ChunkOptions, "chunkSize">;

const account = (id: string, locale?: string) => `/api/accounts/${encodeURIComponent(id)}${locale ? `?locale=${encodeURIComponent(locale)}` : ""}`;
const login = (loginId: string) => `/api/accounts/login/${encodeURIComponent(loginId)}`;
const book = (asin: string) => `/api/library/${encodeURIComponent(asin)}`;

export const api = {
  status: (signal?: AbortSignal) => request<Status>("GET", "/api/status", undefined, signal),
  accounts: (signal?: AbortSignal) => request<Account[]>("GET", "/api/accounts", undefined, signal),
  patchAccount: (id: string, body: AccountPatch, locale?: string) => request<Account>("PATCH", account(id, locale), body),
  deleteAccount: (id: string, locale?: string) => request<Account>("DELETE", account(id, locale)),
  startLogin: (body: LoginStartRequest) => request<LoginStart>("POST", "/api/accounts/login", body),
  completeLogin: (loginId: string, responseUrl: string) => request<Account>("POST", login(loginId), { responseUrl }),
  cancelLogin: (loginId: string) => request<{ loginId: string }>("DELETE", login(loginId)),
  settings: () => request<Settings>("GET", "/api/settings"),
  patchSettings: (body: SettingsPatch) => request<Settings>("PATCH", "/api/settings", body),
  previewTemplate: (body: TemplatePreviewRequest, signal?: AbortSignal) => request<TemplatePreview>("POST", "/api/settings/templates/preview", body, signal),
  absTest: (body: AbsTestRequest) => request<AbsTest>("POST", "/api/audiobookshelf/test", body),
  absLibraries: () => request<AbsLibrary[]>("GET", "/api/audiobookshelf/libraries"),
  absUpload: (asins: string[]) => request<BulkResult>("POST", "/api/audiobookshelf/upload", { asins }),
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
  podcasts: (params: { q: string; trash: boolean }, signal?: AbortSignal) => {
    const qs = new URLSearchParams();
    if (params.q.trim()) qs.set("q", params.q.trim());
    if (params.trash) qs.set("trash", "true");
    return request<Book[]>("GET", `/api/library/podcasts?${qs}`, undefined, signal);
  },
  exportUrl: (format: ExportFormat, params: { q: string; trash: boolean }) => {
    const qs = new URLSearchParams({ format });
    if (params.q.trim()) qs.set("q", params.q.trim());
    if (params.trash) qs.set("trash", "true");
    return `/api/library/export?${qs}`;
  },
  quickFilters: () => request<QuickFilters>("GET", "/api/quick-filters"),
  putQuickFilters: (body: QuickFilters) => request<QuickFilters>("PUT", "/api/quick-filters", body),
  stats: (signal?: AbortSignal) => request<Stats>("GET", "/api/library/stats", undefined, signal),
  coverUrl: (asin: string, size: "small" | "large") => `${book(asin)}/cover?size=${size}`,
  trash: (asin: string) => request<Book>("POST", `${book(asin)}/trash`),
  restore: (asin: string) => request<Book>("POST", `${book(asin)}/restore`),
  liberateBook: (asin: string) => request<unknown>("POST", `${book(asin)}/liberate`),
  getBook: (asin: string, signal?: AbortSignal) => request<BookDetail>("GET", book(asin), undefined, signal),
  setTags: (asin: string, tags: string) => request<Book>("PUT", `${book(asin)}/tags`, { tags }),
  setStatus: (asin: string, status: LiberatedChoice) => request<Book>("PUT", `${book(asin)}/status`, { status }),
  locateFile: (asin: string, path: string) => request<BookDetail>("POST", `${book(asin)}/locate`, { path }),
  startLocateScan: (folder: string) => request<LocateScan>("POST", "/api/library/locate-scan", { folder }),
  locateScan: () => request<LocateScan>("GET", "/api/library/locate-scan"),
  scan: (accounts?: string[]) => request<Status>("POST", "/api/scan", accounts ? { accounts } : undefined),
  liberate: () => request<Status>("POST", "/api/liberate"),
  queueHistory: (signal?: AbortSignal) => request<FinishedJob[]>("GET", "/api/queue/history", undefined, signal),
  clearHistory: () => request<Queue>("DELETE", "/api/queue/history"),
  jobLog: (id: string, signal?: AbortSignal) => request<JobLog>("GET", `/api/queue/jobs/${encodeURIComponent(id)}/log`, undefined, signal),
  cancelJob: (id: string) => request<CancelJob>("DELETE", `/api/queue/jobs/${encodeURIComponent(id)}`),
  cancelAll: () => request<Queue>("POST", "/api/queue/cancel"),
  moveJob: (id: string, to: MoveTo) => request<Queue>("POST", `/api/queue/jobs/${encodeURIComponent(id)}/move`, { to }),
  pauseQueue: () => request<Queue>("POST", "/api/queue/pause"),
  resumeQueue: () => request<Queue>("POST", "/api/queue/resume"),
  decide: (id: number, body: DecisionRequest) => request<Queue>("POST", `/api/queue/decisions/${id}`, body),

  // One function per operation; each chunks at the API's 5000 ASIN limit.
  bulkTrash: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/trash", asins, {}, o),
  bulkRestore: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/restore", asins, {}, o),
  bulkPurge: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/purge", asins, {}, o),
  bulkAbsUpload: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/audiobookshelf/upload", asins, {}, o),
  bulkLiberate: (asins: string[], o?: BulkOpts) => chunked("POST", "/api/library/liberate", asins, {}, o),
  bulkSetStatus: (asins: string[], status: LiberatedChoice, o?: BulkOpts) => chunked("PUT", "/api/library/status", asins, { status }, o),
  bulkSetPdfStatus: (asins: string[], status: PdfChoice, o?: BulkOpts) => chunked("PUT", "/api/library/pdf-status", asins, { status }, o),
  bulkAddTags: (asins: string[], tags: string, o?: BulkOpts) => chunked("PUT", "/api/library/tags", asins, { add: tags }, o),
  bulkRemoveTags: (asins: string[], tags: string, o?: BulkOpts) => chunked("PUT", "/api/library/tags", asins, { remove: tags }, o),
};

function chunked(method: string, path: string, asins: string[], extra: object, o?: BulkOpts): Promise<BulkSummary> {
  return runChunked(asins, (chunk) => request<BulkResult>(method, path, { asins: chunk, ...extra }, o?.signal), o);
}
