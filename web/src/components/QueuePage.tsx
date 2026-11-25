import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { api } from "../api/client";
import type { Book, DecisionRequest, FinishedJob, JobLog, MoveTo, PendingDecision, QueueJob, QueueWait } from "../api/types";
import { formatBytes, formatTime, humanize } from "../format";
import { mergeLog, type LogView } from "../events";
import { useLive } from "../live";
import { BookDetails } from "./BookDetails";

function Decisions({ item }: { item: PendingDecision | QueueWait }) {
  const [error, setError] = useState<string | null>(null);
  const [applyToAll, setApplyToAll] = useState(false);
  const [remember, setRemember] = useState(false);
  const [busy, setBusy] = useState(false);

  const send = async (body: DecisionRequest) => {
    setBusy(true);
    setError(null);
    try {
      await api.decide(item.id, body);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="decisions">
      {item.allowed.map((kind) =>
        kind === "badBook" ? (
          <span key={kind} className="decision-group">
            <label>
              <input type="checkbox" checked={applyToAll} onChange={(e) => setApplyToAll(e.target.checked)} /> apply to all
            </label>
            <label>
              <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> remember
            </label>
            {(["retry", "ignore", "abort"] as const).map((action) => (
              <button key={action} disabled={busy} onClick={() => send({ decision: "badBook", action, applyToAll, remember })}>
                {humanize(action)}
              </button>
            ))}
          </span>
        ) : (
          <button key={kind} disabled={busy} onClick={() => send({ decision: kind })}>
            {humanize(kind)}
          </button>
        ),
      )}
      {error && <span className="error">{error}</span>}
    </div>
  );
}

const moves: { to: MoveTo; label: string; glyph: string }[] = [
  { to: "top", label: "Move to top", glyph: "⇈" },
  { to: "up", label: "Move up", glyph: "↑" },
  { to: "down", label: "Move down", glyph: "↓" },
  { to: "bottom", label: "Move to bottom", glyph: "⇊" },
];

const isError = (f: FinishedJob) => f.status === "failed";

function LogPanel({ jobId, live }: { jobId: string; live?: LogView }) {
  const [server, setServer] = useState<JobLog | null>(null);
  const [error, setError] = useState<string | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const stick = useRef(true);

  useEffect(() => {
    const ctl = new AbortController();
    api.jobLog(jobId, ctl.signal).then(setServer, (e: Error) => !ctl.signal.aborted && setError(e.message));
    return () => ctl.abort();
  }, [jobId]);

  const log = useMemo(() => (server ? mergeLog(server, live) : null), [server, live]);

  useLayoutEffect(() => {
    const el = box.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [log]);

  const onScroll = () => {
    const el = box.current;
    if (el) stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 8;
  };

  if (error) return <p className="error">{error}</p>;
  if (!log) return <p className="muted">Loading log...</p>;
  return (
    <div>
      {log.dropped > 0 && <div className="muted small">{log.dropped} earlier lines dropped</div>}
      <div className="log" ref={box} onScroll={onScroll} role="log" aria-label="Job log">
        {log.lines.length === 0 && <span className="muted">No log lines yet.</span>}
        {log.lines.map((l, i) => (
          <div key={i} className={`log-line level-${l.level}`}>
            <span className="log-time">{new Date(l.at).toLocaleTimeString()}</span> {l.message}
          </div>
        ))}
      </div>
    </div>
  );
}

function JobRow({ job, index, queuedCount, firstQueued, pct, live, onOpen, act }: {
  job: QueueJob;
  index: number;
  queuedCount: number;
  firstQueued: number;
  pct: number | undefined;
  live?: LogView;
  onOpen: (asin: string) => void;
  act: (fn: () => Promise<unknown>) => void;
}) {
  const [showLog, setShowLog] = useState(false);
  const queued = job.state === "queued";
  const pos = index - firstQueued;
  return (
    <li>
      <div className="job-head">
        <button className="linklike job-title" onClick={() => onOpen(job.asin)}>
          {job.title}
        </button>
        <span className="job-actions">
          {queued &&
            moves.map((m) => (
              <button
                key={m.to}
                title={m.label}
                aria-label={`${m.label}: ${job.title}`}
                disabled={m.to === "top" || m.to === "up" ? pos === 0 : pos === queuedCount - 1}
                onClick={() => act(() => api.moveJob(job.id, m.to))}
              >
                {m.glyph}
              </button>
            ))}
          <button aria-expanded={showLog} onClick={() => setShowLog(!showLog)}>
            Log
          </button>
          <button onClick={() => act(() => api.cancelJob(job.id))}>{queued ? "Remove" : "Cancel"}</button>
          <span className={`pill state-${job.state}`}>{job.state}</span>
        </span>
      </div>
      {job.state === "working" && (
        <div className="bar" role="progressbar" aria-valuenow={pct ?? 0} aria-valuemin={0} aria-valuemax={100}>
          <div style={{ width: `${pct ?? 0}%` }} />
          <span>{pct == null ? "starting" : `${pct}%`}</span>
        </div>
      )}
      {showLog && <LogPanel jobId={job.id} live={live} />}
    </li>
  );
}

function HistoryRow({ item, onOpen }: { item: FinishedJob; onOpen: (asin: string) => void }) {
  const [showLog, setShowLog] = useState(false);
  return (
    <li>
      <div className="job-head">
        <button className="linklike job-title" onClick={() => onOpen(item.asin)}>
          {item.title}
        </button>
        <span className="job-actions">
          <span className="muted small">{formatTime(item.finishedAt)}</span>
          <button aria-expanded={showLog} onClick={() => setShowLog(!showLog)}>
            Log
          </button>
          <span className={`pill ${isError(item) ? "book-Error" : item.result === "success" ? "book-Liberated" : ""}`} title={`status: ${item.status}`}>
            {humanize(item.result)}
          </span>
        </span>
      </div>
      {showLog && <LogPanel jobId={item.id} />}
    </li>
  );
}

export function QueuePage({ onSearch }: { onSearch: (q: string) => void }) {
  const { queue, connected, onBookEvent } = useLive();
  const { jobs, progress, pending, waiting, running, paused, stats, logs } = queue;
  const [error, setError] = useState<string | null>(null);
  const [history, setHistory] = useState<FinishedJob[] | null>(null);
  const [opened, setOpened] = useState<Book | null>(null);

  const loadHistory = useCallback(() => {
    api.queueHistory().then(setHistory, (e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    loadHistory();
    return onBookEvent(loadHistory);
  }, [loadHistory, onBookEvent]);

  const act = useCallback(
    (fn: () => Promise<unknown>) => {
      setError(null);
      fn().then(
        () => undefined,
        (e: Error) => setError(e.message),
      ).finally(loadHistory);
    },
    [loadHistory],
  );

  const open = (asin: string) => {
    setError(null);
    api.getBook(asin).then(setOpened, (e: Error) => setError(e.message));
  };

  const firstQueued = jobs.findIndex((j) => j.state === "queued");
  const queuedCount = jobs.filter((j) => j.state === "queued").length;

  return (
    <div className="queue">
      <div className="queue-head">
        <div className="counts">
          <span className="count static">
            <strong>{stats.queued}</strong> queued
          </span>
          <span className="count static ok">
            <strong>{stats.completed}</strong> completed
          </span>
          <span className={`count static ${stats.errors > 0 ? "bad" : ""}`}>
            <strong>{stats.errors}</strong> errors
          </span>
          <span className="count static">{stats.runningSince ? `running since ${formatTime(stats.runningSince)}` : running ? "running" : "idle"}</span>
        </div>
        <div className="row">
          <button onClick={() => act(paused ? api.resumeQueue : api.pauseQueue)}>{paused ? "Resume" : "Pause"}</button>
          <button
            disabled={jobs.length === 0}
            onClick={() => window.confirm("Cancel every queued and running job?") && act(api.cancelAll)}
          >
            Cancel all
          </button>
        </div>
      </div>
      {paused && (
        <div className="card attention" role="status">
          <strong>Queue paused.</strong> <span className="muted">No new books start; running books continue until they finish.</span>
        </div>
      )}
      {!connected && <p className="muted">Live updates are disconnected; showing the last snapshot.</p>}
      {error && <p className="error">{error}</p>}

      {pending.map((p) => (
        <div key={p.id} className="card attention">
          <strong>Decision needed: {humanize(p.reason)}</strong>
          {p.jobId && <span className="muted"> job {jobs.find((j) => j.id === p.jobId)?.title ?? p.jobId}</span>}
          <Decisions item={p} />
        </div>
      ))}

      {waiting && (
        <div className="card attention">
          <strong>Waiting: {humanize(waiting.reason)}</strong>
          <div className="muted">
            since {formatTime(waiting.since)}
            {waiting.nextCheck && `, next check ${formatTime(waiting.nextCheck)}`}
          </div>
          {waiting.volumes?.map((v) => (
            <div key={v.root} className="muted">
              {v.root}: {formatBytes(v.freeBytes)} free, {formatBytes(v.requiredBytes)} required
            </div>
          ))}
          <Decisions item={waiting} />
        </div>
      )}

      <h3>Jobs ({jobs.length})</h3>
      {jobs.length === 0 && <p className="muted">Nothing queued.</p>}
      <ul className="plain jobs">
        {jobs.map((j, i) => (
          <JobRow key={j.id} job={j} index={i} queuedCount={queuedCount} firstQueued={firstQueued} pct={progress[j.id]} live={logs[j.id]} onOpen={open} act={act} />
        ))}
      </ul>

      <div className="job-head">
        <h3>History</h3>
        <button disabled={!history?.length} onClick={() => act(api.clearHistory)}>
          Clear finished
        </button>
      </div>
      {history && history.length === 0 && <p className="muted">No finished jobs.</p>}
      <ul className="plain">
        {history?.map((f) => (
          <HistoryRow key={f.id} item={f} onOpen={open} />
        ))}
      </ul>

      {opened && (
        <BookDetails
          book={opened}
          onChange={setOpened}
          onClose={() => setOpened(null)}
          onSearch={(q) => {
            setOpened(null);
            onSearch(q);
          }}
        />
      )}
    </div>
  );
}
