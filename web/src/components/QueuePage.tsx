import { useState } from "react";
import { api } from "../api/client";
import type { DecisionRequest, PendingDecision, QueueWait } from "../api/types";
import { formatBytes, formatTime, humanize } from "../format";
import { useLive } from "../live";

function Decisions({ item }: { item: PendingDecision | QueueWait }) {
  const { refreshQueue } = useLive();
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
      refreshQueue();
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

export function QueuePage() {
  const { queue, connected } = useLive();
  const { jobs, progress, pending, waiting, running, finished } = queue;

  return (
    <div className="queue">
      <p className="muted">
        {running ? "Queue is running." : "Queue is idle."} {connected ? "" : "Live updates are disconnected; showing the last snapshot."}
      </p>

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
        {jobs.map((j) => {
          const pct = progress[j.id];
          return (
            <li key={j.id}>
              <div className="job-head">
                <span className="job-title">{j.title}</span>
                <span className={`pill state-${j.state}`}>{j.state}</span>
              </div>
              {j.state === "working" && (
                <div className="bar" role="progressbar" aria-valuenow={pct ?? 0} aria-valuemin={0} aria-valuemax={100}>
                  <div style={{ width: `${pct ?? 0}%` }} />
                  <span>{pct == null ? "starting" : `${pct}%`}</span>
                </div>
              )}
            </li>
          );
        })}
      </ul>

      {finished.length > 0 && (
        <>
          <h3>Finished this session</h3>
          <ul className="plain">
            {finished.map((f) => (
              <li key={`${f.jobId}-${f.at}`} className="job-head">
                <span className="job-title">{f.title}</span>
                <span className="pill">{f.result}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
