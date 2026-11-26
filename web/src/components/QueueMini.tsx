import { api } from "../api/client";
import { humanize } from "../format";
import { useLive } from "../live";
import { routeHref } from "../route";
import { Icon } from "./Icon";

/** The inspector's Queue tab: what is running now; the full queue page has history, logs and reordering. */
export function QueueMini() {
  const { queue } = useLive();
  const { jobs, progress, pending, waiting, paused, stats } = queue;
  const attention = pending.length + (waiting ? 1 : 0);

  return (
    <div className="queue-mini">
      {(paused || attention > 0) && (
        <a className="mini-alert" href={routeHref({ page: "queue" })}>
          {paused ? "Queue paused." : `${attention} ${attention === 1 ? "decision" : "decisions"} needed.`} Open the queue
        </a>
      )}
      {jobs.length === 0 ? (
        <div className="empty">
          <Icon name="queue" size={36} />
          <strong>Nothing in the queue</strong>
          <span>Click a book's status light, or use Liberate, to start downloading.</span>
        </div>
      ) : (
        <ul className="plain">
          {jobs.map((j) => {
            const pct = progress[j.id];
            return (
              <li key={j.id} className={`mini-job state-${j.state}`}>
                <div className="mini-job-head">
                  <span className="mini-title">{j.title}</span>
                  <button className="icon-btn small" title={j.state === "queued" ? "Remove from queue" : "Cancel"} aria-label={`${j.state === "queued" ? "Remove" : "Cancel"} ${j.title}`} onClick={() => void api.cancelJob(j.id)}>
                    <Icon name="close" size={14} />
                  </button>
                </div>
                {j.state === "working" ? (
                  <div className="bar thin" role="progressbar" aria-valuenow={pct ?? 0} aria-valuemin={0} aria-valuemax={100}>
                    <div style={{ width: `${pct ?? 0}%` }} />
                  </div>
                ) : (
                  <span className="muted small">{humanize(j.state)}</span>
                )}
              </li>
            );
          })}
        </ul>
      )}
      <div className="mini-foot">
        <span className="muted small">
          {stats.completed} done · {stats.errors} {stats.errors === 1 ? "error" : "errors"}
        </span>
        <a href={routeHref({ page: "queue" })}>Open queue</a>
      </div>
    </div>
  );
}
