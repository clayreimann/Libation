import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { SeriesGroup } from "../api/types";
import { progressLabel } from "../series";

export function SeriesBrowser({ onPick }: { onPick: (seriesId: string) => void }) {
  const [text, setText] = useState("");
  const [list, setList] = useState<SeriesGroup[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ctl = new AbortController();
    const t = window.setTimeout(() => {
      api.series(text, ctl.signal).then(
        (l) => {
          setList(l);
          setError(null);
        },
        (e: Error) => !ctl.signal.aborted && setError(e.message),
      );
    }, 200);
    return () => {
      window.clearTimeout(t);
      ctl.abort();
    };
  }, [text]);

  return (
    <div className="series-browser">
      <input type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder="Series name" aria-label="Filter series" autoFocus />
      {error && <p className="error small">{error}</p>}
      {list && list.length === 0 && <p className="muted small">No series match.</p>}
      <ul className="plain menu-list">
        {list?.map((s) => (
          <li key={s.seriesId}>
            <button onClick={() => onPick(s.seriesId)}>
              {s.name} <span className="muted small">{progressLabel(s)}</span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
