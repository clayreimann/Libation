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
export type BookDetail = Book & Required<Pick<Fix<S["BookDetailDto"]>, "description" | "publisher" | "language" | "copyright" | "isAbridged" | "datePublished" | "categoryLadders" | "audioFileExists" | "partialDownload">>;
export type LocateScan = Fix<S["LocateScanDto"]>;
export type Stats = Fix<S["BackupCountsDto"]>;
export type AsinList = Fix<S["LibraryAsinsDto"]>;
export type LibraryPage = Fix<S["LibraryPageDto"]>;
export type QueueJob = Fix<S["QueueJobDto"]>;
export type PendingDecision = Fix<S["PendingDecisionDto"]>;
export type QueueWait = Fix<S["QueueWaitDto"]>;
export type Queue = Fix<S["QueueDto"]>;
export type BulkResult = Fix<S["BulkResultDto"]>;
export type Settings = Fix<S["SettingsDto"]>;
export interface DecisionRequest {
  decision: string;
  action?: string;
  applyToAll?: boolean;
  remember?: boolean;
}

export type EngineEvent = { at?: string } & (
  | { type: "StateChanged"; state: string }
  | { type: "ScanStarted" }
  | { type: "ScanFinished"; exitCode: number }
  | { type: "LiberateStarted" }
  | { type: "LiberateFinished"; exitCode: number }
  | { type: "CycleFinished"; exitCode: number }
  | { type: "NextRunScheduled"; nextRun: string | null }
  | { type: "BookStarted"; jobId: string; asin: string; title: string }
  | { type: "BookProgress"; jobId: string; asin: string; percent: number }
  | { type: "BookFinished"; jobId: string; asin: string; title: string; result: string; status: string }
  | { type: "QueuePaused"; pause: PendingDecision }
  | { type: "QueueResumed"; pauseId: number; decision: string }
  | { type: "QueueWaiting"; wait: QueueWait }
  | { type: "QueueWaitEnded"; pauseId: number; reason: string }
  | { type: "LocateScanProgress"; scan: LocateScan }
);

export const eventTypes = [
  "StateChanged", "ScanStarted", "ScanFinished", "LiberateStarted", "LiberateFinished", "CycleFinished",
  "NextRunScheduled", "BookStarted", "BookProgress", "BookFinished", "QueuePaused", "QueueResumed",
  "QueueWaiting", "QueueWaitEnded", "LocateScanProgress",
] as const satisfies readonly EngineEvent["type"][];
