import type { components } from "./schema";

type Fix<T> = [number] extends [T]
  ? [string] extends [T]
    ? Exclude<T, string>
    : T
  : T extends readonly (infer U)[]
    ? Fix<U>[]
    : T extends object
      ? { [K in keyof T]: Fix<T[K]> }
      : T;

type S = components["schemas"];

export type Status = Fix<S["StatusDto"]>;
export type Account = Fix<S["AccountDto"]>;
export type Book = Fix<S["BookDto"]>;
export type BookDetail = Fix<S["BookDetailDto"]>;
export type LocateScan = Fix<S["LocateScanDto"]>;
export type Stats = Fix<S["BackupCountsDto"]>;
export type AsinList = Fix<S["LibraryAsinsDto"]>;
export type LibraryPage = Fix<S["LibraryPageDto"]>;
export type QueueJob = Fix<S["QueueJobDto"]>;
export type PendingDecision = Fix<S["PendingDecisionDto"]>;
export type QueueWait = Fix<S["QueueWaitDto"]>;
export type Queue = Fix<S["QueueDto"]>;
export type QueueStats = Fix<S["QueueStatsDto"]>;
export type FinishedJob = Fix<S["FinishedJobDto"]>;
export type JobLog = Fix<S["JobLogDto"]>;
export type JobLogLine = Fix<S["JobLogLineDto"]>;
export type CancelJob = Fix<S["CancelJobDto"]>;
export type MoveTo = "top" | "up" | "down" | "bottom";
export type BulkResult = Fix<S["BulkResultDto"]>;
export type Settings = Fix<S["SettingsDto"]>;
export type SettingsPatch = Fix<S["SettingsPatchRequest"]>;
export type TemplatePreview = Fix<S["TemplatePreviewDto"]>;
export type TemplatePreviewRequest = Fix<S["TemplatePreviewRequest"]>;
export type AbsTest = Fix<S["AudiobookshelfTestDto"]>;
export type AbsTestRequest = Fix<S["AudiobookshelfTestRequest"]>;
export type AbsLibrary = Fix<S["AudiobookshelfLibraryDto"]>;
export type AudioFile = Fix<S["AudioFileDto"]>;
export type Chapter = Fix<S["ChapterDto"]>;
export type DecisionRequest = Fix<S["DecisionRequest"]>;
export type QuickFilter = Fix<S["QuickFilterDto"]>;
export type QuickFilters = Fix<S["QuickFiltersDto"]>;
export type AccountPatch = Fix<S["AccountPatchRequest"]>;
export type ScanRequest = Fix<S["ScanRequest"]>;
export type LoginStart = Fix<S["LoginStartDto"]>;
export type LoginStartRequest = Fix<S["LoginStartRequest"]>;
export type AuthState = Account["authState"];

const eventNames = [
  "StateChanged", "ScanStarted", "ScanFinished", "LiberateStarted", "LiberateFinished", "CycleFinished",
  "NextRunScheduled", "BookStarted", "BookProgress", "BookFinished", "QueueChanged", "QueuePaused", "QueueResumed",
  "QueueWaiting", "QueueWaitEnded", "QueueDispatchPaused", "QueueDispatchResumed", "BookLog", "LocateScanProgress", "AccountNeedsLogin",
] as const satisfies readonly (keyof S)[];

type EventName = (typeof eventNames)[number];

/** Generated event bodies tagged with the SSE event name. */
export type EngineEvent = { [K in EventName]: { type: K } & Fix<S[K]> }[EventName];

type Uncovered = Exclude<S["EngineEvent"], S[EventName]>;
const covered: [Uncovered] extends [never] ? true : never = true;
void covered;

export const eventTypes: readonly EventName[] = eventNames;
