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
export type BookDetail = Book & Pick<Fix<S["BookDetailDto"]>, "description" | "publisher" | "language" | "copyright" | "isAbridged" | "datePublished" | "categoryLadders" | "audioFileExists">;
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
export type DecisionRequest = Partial<Fix<S["DecisionRequest"]>> & { decision: string };
export type QuickFilter = Fix<S["QuickFilterDto"]>;
export type QuickFilters = Fix<S["QuickFiltersDto"]>;

const eventNames = [
  "StateChanged", "ScanStarted", "ScanFinished", "LiberateStarted", "LiberateFinished", "CycleFinished",
  "NextRunScheduled", "BookStarted", "BookProgress", "BookFinished", "QueueChanged", "QueuePaused", "QueueResumed",
  "QueueWaiting", "QueueWaitEnded", "QueueDispatchPaused", "QueueDispatchResumed", "BookLog", "LocateScanProgress",
] as const satisfies readonly (keyof S)[];

// the generated event schemas inline these DTOs without their enums
interface Nested {
  QueuePaused: { pause: PendingDecision };
  QueueChanged: { queue: Queue };
  QueueWaiting: { wait: QueueWait };
  LocateScanProgress: { scan: LocateScan };
}

type EventName = (typeof eventNames)[number];

/** Generated event bodies tagged with the SSE event name. */
export type EngineEvent = { [K in EventName]: { type: K } & Omit<Fix<S[K]>, K extends keyof Nested ? keyof Nested[K] : never> & (K extends keyof Nested ? Nested[K] : unknown) }[EventName];

type Uncovered = Exclude<S["EngineEvent"], S[EventName]>;
const covered: [Uncovered] extends [never] ? true : never = true;
void covered;

export const eventTypes: readonly EventName[] = eventNames;
