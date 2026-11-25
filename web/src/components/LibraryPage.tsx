import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type { Book, LibraryPage as Page } from "../api/types";
import { runBulk } from "../bulk";
import { formatLength } from "../format";
import { useLive } from "../live";

const PAGE_SIZE = 50;
const ALL_PAGE = 1000;

interface BulkState {
  label: string;
  done: number;
  total: number;
  running: boolean;
  failed: { item: string; error: string }[];
  cancelled: boolean;
}

function seriesText(b: Book): string {
  return b.series.map((s) => (s.order ? `${s.name} #${s.order}` : s.name)).join(", ");
}

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

export function LibraryPage() {
  const { onBookEvent } = useLive();
  const [input, setInput] = useState("");
  const q = useDebounced(input, 300);
  const [trash, setTrash] = useState(false);
  const [offset, setOffset] = useState(0);
  const [page, setPage] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [reload, setReload] = useState(0);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulk, setBulk] = useState<BulkState | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [gathering, setGathering] = useState(false);
  const abort = useRef<AbortController | null>(null);

  const effectiveQ = trash ? "" : q;

  useEffect(() => {
    const ctl = new AbortController();
    setLoading(true);
    api.library({ offset, limit: PAGE_SIZE, q: effectiveQ, trash }, ctl.signal).then(
      (p) => {
        setPage(p);
        setError(null);
        setLoading(false);
      },
      (e: Error) => {
        if (ctl.signal.aborted) return;
        setError(e.message);
        setLoading(false);
      },
    );
    return () => ctl.abort();
  }, [offset, effectiveQ, trash, reload]);

  useEffect(() => onBookEvent(() => setReload((n) => n + 1)), [onBookEvent]);

  const resetView = () => {
    setOffset(0);
    setSelected(new Set());
  };

  const items = page?.items ?? [];
  const total = page?.total ?? 0;
  const pageAsins = items.map((b) => b.asin);
  const allOnPage = pageAsins.length > 0 && pageAsins.every((a) => selected.has(a));

  const toggle = (asin: string) =>
    setSelected((s) => {
      const n = new Set(s);
      if (!n.delete(asin)) n.add(asin);
      return n;
    });

  const togglePage = () =>
    setSelected((s) => {
      const n = new Set(s);
      for (const a of pageAsins) allOnPage ? n.delete(a) : n.add(a);
      return n;
    });

  const selectAllMatching = async () => {
    setGathering(true);
    setNotice(null);
    try {
      const all = new Set<string>();
      for (let o = 0; o < total; o += ALL_PAGE) {
        const p = await api.library({ offset: o, limit: ALL_PAGE, q: effectiveQ, trash });
        p.items.forEach((b) => all.add(b.asin));
        if (p.items.length === 0) break;
      }
      setSelected(all);
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setGathering(false);
    }
  };

  const runAction = useCallback(async (label: string, asins: string[], worker: (asin: string) => Promise<unknown>) => {
    const ctl = new AbortController();
    abort.current = ctl;
    setBulk({ label, done: 0, total: asins.length, running: true, failed: [], cancelled: false });
    const res = await runBulk(asins, {
      concurrency: 4,
      signal: ctl.signal,
      worker,
      onProgress: (done, total) => setBulk((b) => (b ? { ...b, done, total } : b)),
    });
    setBulk({
      label,
      done: res.ok + res.failed.length,
      total: asins.length,
      running: false,
      failed: res.failed.map((f) => ({ item: f.item, error: f.error })),
      cancelled: res.cancelled,
    });
    setSelected(new Set(res.failed.map((f) => f.item)));
    setReload((n) => n + 1);
  }, []);

  const bulkTrash = () => runAction(trash ? "Restoring" : "Trashing", [...selected], trash ? api.restore : api.trash);

  const rowAction = async (label: string, fn: () => Promise<unknown>) => {
    setNotice(null);
    try {
      await fn();
      setReload((n) => n + 1);
    } catch (e) {
      setNotice(`${label}: ${(e as Error).message}`);
    }
  };

  const busy = bulk?.running === true;
  const end = Math.min(offset + PAGE_SIZE, total);

  return (
    <div className="library">
      <div className="toolbar">
        <div className="seg">
          <button className={!trash ? "active" : ""} onClick={() => { setTrash(false); resetView(); }}>
            Library
          </button>
          <button className={trash ? "active" : ""} onClick={() => { setTrash(true); resetView(); }}>
            Trash
          </button>
        </div>
        <input
          type="search"
          className={`search ${error && !trash ? "invalid" : ""}`}
          placeholder={trash ? "Search is unavailable in the trash" : 'Filter, e.g. author:herbert -tags:skip title:"the hobbit"'}
          value={input}
          disabled={trash}
          onChange={(e) => {
            setInput(e.target.value);
            resetView();
          }}
          aria-label="Search library"
        />
      </div>
      {error && <p className="error">{error}</p>}
      {notice && <p className="error">{notice}</p>}

      {selected.size > 0 && (
        <div className="bulkbar">
          <strong>{selected.size} selected</strong>
          {selected.size < total && !busy && (
            <button onClick={selectAllMatching} disabled={gathering}>
              {gathering ? "Gathering..." : `Select all ${total} matching`}
            </button>
          )}
          <button onClick={() => setSelected(new Set())} disabled={busy}>
            Clear
          </button>
          <button className="primary" onClick={bulkTrash} disabled={busy}>
            {trash ? "Restore" : "Move to trash"} ({selected.size})
          </button>
        </div>
      )}

      {bulk && (
        <div className="card">
          <div className="job-head">
            <span>
              {bulk.running ? `${bulk.label}...` : bulk.cancelled ? "Cancelled" : "Done"} {bulk.done} / {bulk.total}
            </span>
            {bulk.running ? (
              <button onClick={() => abort.current?.abort()}>Cancel</button>
            ) : (
              <button onClick={() => setBulk(null)}>Dismiss</button>
            )}
          </div>
          <div className="bar">
            <div style={{ width: `${bulk.total ? (bulk.done / bulk.total) * 100 : 0}%` }} />
          </div>
          {!bulk.running && bulk.failed.length > 0 && (
            <details open>
              <summary className="error">{bulk.failed.length} failed (still selected)</summary>
              <ul className="plain">
                {bulk.failed.slice(0, 20).map((f) => (
                  <li key={f.item}>
                    {f.item}: {f.error}
                  </li>
                ))}
              </ul>
            </details>
          )}
        </div>
      )}

      <div className="tablewrap" aria-busy={loading}>
        <table>
          <thead>
            <tr>
              <th className="check">
                <input type="checkbox" checked={allOnPage} onChange={togglePage} disabled={items.length === 0 || busy} aria-label="Select page" />
              </th>
              <th>Title</th>
              <th className="hide-sm">Authors</th>
              <th className="hide-sm">Series</th>
              <th className="hide-sm">Length</th>
              <th>Status</th>
              <th className="hide-sm">Tags</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((b) => (
              <tr key={b.asin} className={selected.has(b.asin) ? "selected" : ""}>
                <td className="check">
                  <input type="checkbox" checked={selected.has(b.asin)} onChange={() => toggle(b.asin)} disabled={busy} aria-label={`Select ${b.title}`} />
                </td>
                <td>
                  <div className="title">{b.title}</div>
                  {b.subtitle && <div className="muted small">{b.subtitle}</div>}
                  <div className="show-sm muted small">{[...b.authors, formatLength(b.lengthInMinutes)].join(" \u00b7 ")}</div>
                </td>
                <td className="hide-sm">{b.authors.join(", ")}</td>
                <td className="hide-sm">{seriesText(b)}</td>
                <td className="nowrap hide-sm">{formatLength(b.lengthInMinutes)}</td>
                <td>
                  <span className={`pill book-${b.bookStatus}`}>{b.bookStatus}</span>
                </td>
                <td className="hide-sm">{b.tags}</td>
                <td className="actions">
                  {!b.inTrash && (
                    <button onClick={() => rowAction("Liberate", () => api.liberateBook(b.asin))} disabled={busy}>
                      Liberate
                    </button>
                  )}
                  <button onClick={() => rowAction(b.inTrash ? "Restore" : "Trash", () => (b.inTrash ? api.restore(b.asin) : api.trash(b.asin)))} disabled={busy}>
                    {b.inTrash ? "Restore" : "Trash"}
                  </button>
                </td>
              </tr>
            ))}
            {page && items.length === 0 && (
              <tr>
                <td colSpan={8} className="muted">
                  {trash ? "The trash is empty." : "No books match."}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="pager">
        <span className="muted">{total === 0 ? "0 books" : `${offset + 1}-${end} of ${total}`}</span>
        <button onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))} disabled={offset === 0}>
          Previous
        </button>
        <button onClick={() => setOffset(offset + PAGE_SIZE)} disabled={offset + PAGE_SIZE >= total}>
          Next
        </button>
      </div>
    </div>
  );
}
