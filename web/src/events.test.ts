import { describe, expect, it } from "vitest";
import type { EngineEvent, Queue } from "./api/types";
import { applyQueueEvent, applySnapshot, applyStatusEvent, emptyQueue, needsQueueRefetch, parseEvent } from "./events";

const started: EngineEvent = { type: "BookStarted", jobId: "j1", asin: "A1", title: "One" };

describe("queue reducer", () => {
  it("tracks a job from start through progress to finish", () => {
    let v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookProgress", jobId: "j1", asin: "A1", percent: 40 });
    expect(v.jobs).toEqual([{ id: "j1", asin: "A1", title: "One", state: "working" }]);
    expect(v.progress.j1).toBe(40);

    v = applyQueueEvent(v, { type: "BookFinished", jobId: "j1", asin: "A1", title: "One", result: "success", status: "Liberated" });
    expect(v.jobs).toEqual([]);
    expect(v.progress).toEqual({});
    expect(v.finished[0]?.title).toBe("One");
  });

  it("promotes a queued job instead of duplicating it", () => {
    const v0 = applySnapshot(emptyQueue, { running: true, jobs: [{ id: "j1", asin: "A1", title: "One", state: "queued" }], pending: [], waiting: null });
    expect(applyQueueEvent(v0, started).jobs).toHaveLength(1);
    expect(applyQueueEvent(v0, started).jobs[0]?.state).toBe("working");
  });

  it("adds, dedupes and clears pauses and waits", () => {
    const pause = { id: 7, reason: "badBook", jobId: "j1", allowed: ["badBook"] };
    let v = applyQueueEvent(emptyQueue, { type: "QueuePaused", pause });
    v = applyQueueEvent(v, { type: "QueuePaused", pause });
    expect(v.pending).toHaveLength(1);
    v = applyQueueEvent(v, { type: "QueueResumed", pauseId: 7, decision: "badBook" });
    expect(v.pending).toEqual([]);

    const wait = { id: 9, reason: "diskFull", since: "2026-01-01T00:00:00Z", nextCheck: null, jobId: null, allowed: ["stop"], volumes: null };
    v = applyQueueEvent(v, { type: "QueueWaiting", wait });
    v = applyQueueEvent(v, { type: "QueueWaitEnded", pauseId: 1, reason: "x" });
    expect(v.waiting?.id).toBe(9);
    v = applyQueueEvent(v, { type: "QueueWaitEnded", pauseId: 9, reason: "x" });
    expect(v.waiting).toBeNull();
  });

  it("snapshot drops progress of jobs that are gone but keeps history", () => {
    let v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookProgress", jobId: "j1", asin: "A1", percent: 10 });
    v = { ...v, finished: [{ jobId: "x", asin: "X", title: "X", result: "ok", at: 1 }] };
    const snap: Queue = { running: false, jobs: [], pending: [], waiting: null };
    const out = applySnapshot(v, snap);
    expect(out.progress).toEqual({});
    expect(out.finished).toHaveLength(1);
  });
});

describe("status and parsing", () => {
  const status = { state: "idle", lastRun: null, lastSuccess: null, scanExitCode: null, liberateExitCode: null, nextRun: null, intervalSeconds: 60, version: "1" };

  it("applies state and next-run events", () => {
    expect(applyStatusEvent(status, { type: "StateChanged", state: "scanning" })?.state).toBe("scanning");
    expect(applyStatusEvent(status, { type: "NextRunScheduled", nextRun: "2026-01-01T00:00:00Z" })?.nextRun).toBe("2026-01-01T00:00:00Z");
    expect(applyStatusEvent(null, { type: "StateChanged", state: "x" })).toBeNull();
  });

  it("parses SSE name and data into a typed event", () => {
    const evt = parseEvent("BookProgress", '{"jobId":"j","asin":"A","percent":5,"at":"2026-01-01T00:00:00Z"}');
    expect(evt).toMatchObject({ type: "BookProgress", percent: 5 });
    expect(parseEvent("BookProgress", "nope")).toBeNull();
  });

  it("skips queue refetch for progress ticks only", () => {
    expect(needsQueueRefetch({ type: "BookProgress", jobId: "j", asin: "A", percent: 1 })).toBe(false);
    expect(needsQueueRefetch(started)).toBe(true);
  });
});
