import { describe, expect, it } from "vitest";
import type { EngineEvent, Queue } from "./api/types";
import { appendLog, applyQueueEvent as applyRaw, applySnapshot, applyStatusEvent as applyStatusRaw, emptyQueue, emptyStats, MAX_LOG_LINES, mergeLog, parseEvent, type QueueView } from "./events";

type Body = EngineEvent extends infer E ? (E extends EngineEvent ? Omit<E, "at"> & { at?: string } : never) : never;
const ev = (e: Body): EngineEvent => ({ at: "2026-01-01T00:00:00Z", ...e }) as EngineEvent;
const applyQueueEvent = (v: QueueView, e: Body) => applyRaw(v, ev(e));
const applyStatusEvent = (s: Parameters<typeof applyStatusRaw>[0], e: Body) => applyStatusRaw(s, ev(e));

const started: Body = { type: "BookStarted", jobId: "j1", asin: "A1", title: "One" };

describe("queue reducer", () => {
  it("tracks a job from start through progress to finish", () => {
    let v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookProgress", jobId: "j1", asin: "A1", percent: 40 });
    expect(v.jobs).toEqual([{ id: "j1", asin: "A1", title: "One", state: "working" }]);
    expect(v.progress.j1).toBe(40);

    v = applyQueueEvent(v, { type: "BookFinished", jobId: "j1", asin: "A1", title: "One", result: "success", status: "completed" });
    expect(v.jobs).toEqual([]);
    expect(v.progress).toEqual({});
  });

  it("promotes a queued job instead of duplicating it", () => {
    const v0 = applySnapshot(emptyQueue, { running: true, jobs: [{ id: "j1", asin: "A1", title: "One", state: "queued" }], pending: [], waiting: null, paused: false, stats: emptyStats });
    expect(applyQueueEvent(v0, started).jobs).toHaveLength(1);
    expect(applyQueueEvent(v0, started).jobs[0]?.state).toBe("working");
  });

  it("adds, dedupes and clears pauses and waits", () => {
    const pause = { id: 7, reason: "badBook" as const, jobId: "j1", allowed: ["badBook"] };
    let v = applyQueueEvent(emptyQueue, { type: "QueuePaused", pause });
    v = applyQueueEvent(v, { type: "QueuePaused", pause });
    expect(v.pending).toHaveLength(1);
    v = applyQueueEvent(v, { type: "QueueResumed", pauseId: 7, decision: "badBook" });
    expect(v.pending).toEqual([]);

    const wait = { id: 9, reason: "diskFull" as const, since: "2026-01-01T00:00:00Z", nextCheck: null, jobId: null, allowed: ["stop"], volumes: null };
    v = applyQueueEvent(v, { type: "QueueWaiting", wait });
    v = applyQueueEvent(v, { type: "QueueWaitEnded", pauseId: 1, reason: "x" });
    expect(v.waiting?.id).toBe(9);
    v = applyQueueEvent(v, { type: "QueueWaitEnded", pauseId: 9, reason: "x" });
    expect(v.waiting).toBeNull();
  });

  it("snapshot drops progress and logs of jobs that are gone", () => {
    let v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookProgress", jobId: "j1", asin: "A1", percent: 10 });
    v = applyQueueEvent(v, { type: "BookLog", jobId: "j1", level: "info", message: "hi", at: "2026-01-01T00:00:00Z" });
    const snap: Queue = { running: false, jobs: [], pending: [], waiting: null, paused: false, stats: emptyStats };
    const out = applySnapshot(v, snap);
    expect(out.progress).toEqual({});
    expect(out.logs).toEqual({});
  });

  it("takes paused and stats from the snapshot", () => {
    const stats = { queued: 2, completed: 3, errors: 1, runningSince: "2026-01-01T00:00:00Z" };
    const out = applySnapshot(emptyQueue, { running: true, jobs: [], pending: [], waiting: null, paused: true, stats });
    expect(out.paused).toBe(true);
    expect(out.stats).toEqual(stats);
  });

  it("applies a QueueChanged snapshot, keeping progress only for surviving jobs", () => {
    let v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookProgress", jobId: "j1", asin: "A1", percent: 10 });
    const queue: Queue = { running: true, paused: true, jobs: [{ id: "j1", asin: "A1", title: "One", state: "working" }, { id: "j2", asin: "A2", title: "Two", state: "queued" }], pending: [], waiting: null, stats: { ...emptyStats, queued: 1 } };
    const out = applyQueueEvent(v, { type: "QueueChanged", queue });
    expect(out.jobs.map((j) => j.id)).toEqual(["j1", "j2"]);
    expect(out.paused).toBe(true);
    expect(out.progress.j1).toBe(10);
    expect(applyQueueEvent(out, { type: "QueueChanged", queue: { ...queue, jobs: [] } }).progress).toEqual({});
  });

  it("toggles dispatch pause from events", () => {
    let v = applyQueueEvent(emptyQueue, { type: "QueueDispatchPaused" });
    expect(v.paused).toBe(true);
    expect(applyQueueEvent(v, { type: "QueueDispatchPaused" })).toBe(v);
    v = applyQueueEvent(v, { type: "QueueDispatchResumed" });
    expect(v.paused).toBe(false);
  });
});

describe("job logs", () => {
  const line = (n: number) => ({ at: `2026-01-01T00:00:${String(n % 60).padStart(2, "0")}Z`, level: "info" as const, message: `m${n}` });

  it("appends BookLog lines for known jobs only", () => {
    let v = applyQueueEvent(emptyQueue, { type: "BookLog", jobId: "j1", level: "info", message: "early" });
    expect(v.logs).toEqual({});
    v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookLog", jobId: "j1", level: "warning", message: "careful", at: "2026-01-01T00:00:00Z" });
    expect(v.logs.j1).toEqual({ dropped: 0, lines: [{ at: "2026-01-01T00:00:00Z", level: "warning", message: "careful" }] });
  });

  it("caps at the limit and counts dropped lines", () => {
    let log = appendLog(undefined, line(0));
    for (let i = 1; i < MAX_LOG_LINES + 3; i++) log = appendLog(log, line(i));
    expect(log.lines).toHaveLength(MAX_LOG_LINES);
    expect(log.dropped).toBe(3);
    expect(log.lines[0]?.message).toBe("m3");
  });

  it("clears the live log when the job finishes", () => {
    let v = applyQueueEvent(emptyQueue, started);
    v = applyQueueEvent(v, { type: "BookLog", jobId: "j1", level: "info", message: "x" });
    v = applyQueueEvent(v, { type: "BookFinished", jobId: "j1", asin: "A1", title: "One", result: "success", status: "completed" });
    expect(v.logs).toEqual({});
  });

  it("merges a fetched log with live lines without duplicating", () => {
    const server = { jobId: "j1", dropped: 2, lines: [line(1), line(2)] };
    const live = { dropped: 0, lines: [line(2), line(3)] };
    const merged = mergeLog(server, live);
    expect(merged.lines.map((l) => l.message)).toEqual(["m1", "m2", "m3"]);
    expect(merged.dropped).toBe(2);
    expect(mergeLog(server, undefined).lines).toHaveLength(2);
  });
});

describe("status and parsing", () => {
  const status = { state: "idle", lastRun: null, lastSuccess: null, scanExitCode: null, liberateExitCode: null, nextRun: null, intervalSeconds: 60, version: "1", accountsNeedingLogin: [], accountsManagedByImport: false };

  it("applies state and next-run events", () => {
    expect(applyStatusEvent(status, { type: "StateChanged", state: "scanning" })?.state).toBe("scanning");
    expect(applyStatusEvent(status, { type: "NextRunScheduled", nextRun: "2026-01-01T00:00:00Z" })?.nextRun).toBe("2026-01-01T00:00:00Z");
    expect(applyStatusEvent(null, { type: "StateChanged", state: "x" })).toBeNull();
  });

  it("flags an account from AccountNeedsLogin", () => {
    const s = applyStatusEvent(status, { type: "AccountNeedsLogin", accountId: "a@x", at: "2026-01-01T00:00:00Z" });
    expect(s?.accountsNeedingLogin).toEqual(["a@x"]);
  });

  it("parses SSE name and data into a typed event", () => {
    const evt = parseEvent("BookProgress", '{"jobId":"j","asin":"A","percent":5,"at":"2026-01-01T00:00:00Z"}');
    expect(evt).toMatchObject({ type: "BookProgress", percent: 5 });
    expect(parseEvent("BookProgress", "nope")).toBeNull();
  });
});

describe("locate scan event", () => {
  it("is subscribed and parsed", async () => {
    const { eventTypes } = await import("./api/types");
    expect(eventTypes).toContain("LocateScanProgress");
    expect(eventTypes).toContain("QualityScanProgress");
    expect(eventTypes).toEqual(expect.arrayContaining(["BookLog", "QueueDispatchPaused", "QueueDispatchResumed"]));
    const evt = parseEvent("LocateScanProgress", '{"scan":{"state":"running","found":2}}');
    expect(evt).toMatchObject({ type: "LocateScanProgress", scan: { found: 2 } });
  });
});
