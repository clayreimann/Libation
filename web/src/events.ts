import type { EngineEvent, JobLog, JobLogLine, PendingDecision, Queue, QueueJob, QueueStats, QueueWait, Status } from "./api/types";

export interface LogView {
  dropped: number;
  lines: JobLogLine[];
}

export interface QueueView {
  jobs: QueueJob[];
  progress: Record<string, number>;
  pending: PendingDecision[];
  waiting: QueueWait | null;
  running: boolean;
  paused: boolean;
  stats: QueueStats;
  logs: Record<string, LogView>;
}

export const emptyStats: QueueStats = { queued: 0, completed: 0, errors: 0, runningSince: null };
export const emptyQueue: QueueView = { jobs: [], progress: {}, pending: [], waiting: null, running: false, paused: false, stats: emptyStats, logs: {} };

export const MAX_LOG_LINES = 500;

/** Appends a line, dropping the oldest beyond the cap and counting them. */
export function appendLog(log: LogView | undefined, line: JobLogLine): LogView {
  const lines = [...(log?.lines ?? []), line];
  const over = Math.max(0, lines.length - MAX_LOG_LINES);
  return { dropped: (log?.dropped ?? 0) + over, lines: over ? lines.slice(over) : lines };
}

const lineKey = (l: JobLogLine) => `${l.at}|${l.level}|${l.message}`;

/** Server snapshot plus live lines that arrived while it was in flight, without duplicates. */
export function mergeLog(server: JobLog, live: LogView | undefined): LogView {
  const seen = new Set(server.lines.map(lineKey));
  let log: LogView = { dropped: server.dropped, lines: server.lines.slice(-MAX_LOG_LINES) };
  for (const l of live?.lines ?? []) if (!seen.has(lineKey(l))) log = appendLog(log, l);
  return log;
}

/** Replaces server-owned fields; keeps progress and live logs only for jobs still present. */
export function applySnapshot(view: QueueView, snap: Queue): QueueView {
  const ids = new Set(snap.jobs.map((j) => j.id));
  const progress = Object.fromEntries(Object.entries(view.progress).filter(([id]) => ids.has(id)));
  const logs = Object.fromEntries(Object.entries(view.logs).filter(([id]) => ids.has(id)));
  return { ...view, jobs: snap.jobs, pending: snap.pending, waiting: snap.waiting, running: snap.running, paused: snap.paused, stats: snap.stats, progress, logs };
}

export function applyQueueEvent(view: QueueView, evt: EngineEvent): QueueView {
  switch (evt.type) {
    case "BookStarted": {
      const known = view.jobs.some((j) => j.id === evt.jobId);
      const jobs = known
        ? view.jobs.map((j) => (j.id === evt.jobId ? { ...j, state: "working" } : j))
        : [...view.jobs, { id: evt.jobId, asin: evt.asin, title: evt.title, state: "working" }];
      return { ...view, jobs, running: true };
    }
    case "BookProgress":
      return { ...view, progress: { ...view.progress, [evt.jobId]: evt.percent } };
    case "BookFinished": {
      const { [evt.jobId]: _p, ...progress } = view.progress;
      const { [evt.jobId]: _l, ...logs } = view.logs;
      return { ...view, jobs: view.jobs.filter((j) => j.id !== evt.jobId), progress, logs };
    }
    case "BookLog": {
      if (!view.jobs.some((j) => j.id === evt.jobId)) return view;
      const line = { at: evt.at ?? new Date().toISOString(), level: evt.level, message: evt.message };
      return { ...view, logs: { ...view.logs, [evt.jobId]: appendLog(view.logs[evt.jobId], line) } };
    }
    case "QueueDispatchPaused":
      return view.paused ? view : { ...view, paused: true };
    case "QueueDispatchResumed":
      return view.paused ? { ...view, paused: false } : view;
    case "QueuePaused":
      return view.pending.some((p) => p.id === evt.pause.id) ? view : { ...view, pending: [...view.pending, evt.pause] };
    case "QueueResumed":
      return { ...view, pending: view.pending.filter((p) => p.id !== evt.pauseId) };
    case "QueueWaiting":
      return { ...view, waiting: evt.wait };
    case "QueueWaitEnded":
      return view.waiting?.id === evt.pauseId ? { ...view, waiting: null } : view;
    default:
      return view;
  }
}

export function applyStatusEvent(status: Status | null, evt: EngineEvent): Status | null {
  if (!status) return status;
  switch (evt.type) {
    case "StateChanged":
      return { ...status, state: evt.state };
    case "NextRunScheduled":
      return { ...status, nextRun: evt.nextRun };
    default:
      return status;
  }
}

/** Events after which the server's view can differ from what the reducers derive. */
export function needsQueueRefetch(evt: EngineEvent): boolean {
  if (evt.type === "BookProgress" || evt.type === "BookLog") return false;
  return evt.type.startsWith("Book") || evt.type.startsWith("Queue") || evt.type === "LiberateFinished";
}

export function needsStatusRefetch(evt: EngineEvent): boolean {
  return evt.type === "ScanFinished" || evt.type === "LiberateFinished" || evt.type === "CycleFinished";
}

export function parseEvent(name: string, data: string): EngineEvent | null {
  try {
    const body = JSON.parse(data) as Record<string, unknown>;
    return { ...body, type: name } as EngineEvent;
  } catch {
    return null;
  }
}
