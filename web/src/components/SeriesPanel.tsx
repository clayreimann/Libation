import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Book } from "../api/types";
import { seriesQuery } from "../filters";
import { positionLabel, progressLabel } from "../series";

interface Entry {
  asin: string;
  title: string;
  order?: string | null;
}

interface Loaded {
  id: string;
  name: string;
  summary: string;
  entries: Entry[];
}

interface Props {
  book: Book;
  onShow: (seriesId: string) => void;
}

async function load(book: Book, s: Book["series"][number], signal: AbortSignal): Promise<Loaded> {
  if (s.asin === book.parentAsin) {
    const eps = await api.podcastEpisodes(s.asin, signal);
    return { id: s.asin, name: s.name, summary: `${eps.length} episodes, newest first`, entries: eps.map((e) => ({ asin: e.asin, title: e.title, order: e.series.find((x) => x.asin === s.asin)?.order })) };
  }
  const g = await api.seriesById(s.asin, signal);
  return { id: g.seriesId, name: g.name, summary: progressLabel(g), entries: g.books };
}

export function SeriesPanel({ book, onShow }: Props) {
  const [loaded, setLoaded] = useState<Loaded[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ctl = new AbortController();
    setLoaded([]);
    setError(null);
    Promise.all(book.series.map((s) => load(book, s, ctl.signal))).then(setLoaded, (e: Error) => !ctl.signal.aborted && setError(e.message));
    return () => ctl.abort();
  }, [book.asin, book.series, book.parentAsin]);

  if (!book.series.length) return null;
  return (
    <>
      <h3>Series</h3>
      {error && <p className="error">Series unavailable: {error}</p>}
      {loaded.map((s) => (
        <div key={s.id} className="series-block">
          <div className="job-head">
            <strong>{s.name}</strong>
            <button onClick={() => onShow(s.id)} title={seriesQuery(s.id)}>
              Show in library
            </button>
          </div>
          <p className="muted small">{s.summary}</p>
          <ol className="series-books">
            {s.entries.map((e) => (
              <li key={e.asin} className={e.asin === book.asin ? "current" : ""}>
                <span className="series-pos">{positionLabel(e.order)}</span> {e.title}
              </li>
            ))}
          </ol>
        </div>
      ))}
    </>
  );
}
