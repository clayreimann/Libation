import type { EngineEvent, PendingDecision, Queue, QueueJob, QueueWait, Status } from "./api/types";

export interface Finished {
  jobId: string;
  asin: string;
  title: string;
  result: string;
  at: number;
}

export interface QueueView {
  jobs: QueueJob[];
  progress: Record<string, number>;
  pending: PendingDecision[];
  waiting: QueueWait | null;
  running: boolean;
  finished: Finished[];
}

export const emptyQueue: QueueView = { jobs: [], progress: {}, pending: [], waiting: null, running: false, finished: [] };

const MAX_FINISHED = 20;

/** Replaces server-owned fields; keeps progress for jobs still present and the finished history. */
export function applySnapshot(view: QueueView, snap: Queue): QueueView {
  const ids = new Set(snap.jobs.map((j) => j.id));
  const progress = Object.fromEntries(Object.entries(view.progress).filter(([id]) => ids.has(id)));
  return { ...view, jobs: snap.jobs, pending: snap.pending, waiting: snap.waiting, running: snap.running, progress };
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
      const { [evt.jobId]: _, ...progress } = view.progress;
      const done: Finished = { jobId: evt.jobId, asin: evt.asin, title: evt.title, result: evt.result, at: Date.parse(evt.at ?? "") || Date.now() };
      return {
        ...view,
        jobs: view.jobs.filter((j) => j.id !== evt.jobId),
        progress,
        finished: [done, ...view.finished].slice(0, MAX_FINISHED),
      };
    }
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
  if (evt.type === "BookProgress") return false;
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
