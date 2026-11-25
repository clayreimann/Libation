import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type { Book, LibraryPage as Page } from "../api/types";
import { notOk, type BulkSummary } from "../bulk";
import { columns, loadVisible, saveVisible, toggleColumn, type ColumnId } from "../columns";
import { seriesQuery, withHideEpisodes } from "../filters";
import { formatDate, formatLength, stars } from "../format";
import { loadSort, saveSort, toggleSort, type SortKey, type SortState } from "../sort";
import { useLive } from "../live";
import { BookDetails } from "./BookDetails";
import { BulkMenu, type BulkOp } from "./BulkMenu";
import { CountsBar } from "./CountsBar";
import { Cover } from "./Cover";
import { LocatePanel } from "./LocatePanel";
import { Popover } from "./Popover";
import { QuickFilters } from "./QuickFilters";
import { SyntaxHelp } from "./SyntaxHelp";

const PAGE_SIZE = 50;

interface BulkState {
  label: string;
  done: number;
  total: number;
  running: boolean;
  summary: BulkSummary | null;
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
  const [hideEpisodes, setHideEpisodes] = useState(false);
  const [detail, setDetail] = useState<Book | null>(null);
  const [sort, setSort] = useState<SortState | null>(loadSort);
  const [visible, setVisible] = useState<ColumnId[]>(loadVisible);
  const abort = useRef<AbortController | null>(null);

  const effectiveQ = withHideEpisodes(q, hideEpisodes);
  const show = (id: ColumnId) => visible.includes(id);

  useEffect(() => {
    const ctl = new AbortController();
    setLoading(true);
    api.library({ offset, limit: PAGE_SIZE, q: effectiveQ, trash, sort }, ctl.signal).then(
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
  }, [offset, effectiveQ, trash, sort, reload]);

  const patchRow = (b: Book) => {
    setDetail((d) => (d?.asin === b.asin ? b : d));
    setPage((p) => (p ? { ...p, items: p.items.map((i) => (i.asin === b.asin ? b : i)) } : p));
    setReload((n) => n + 1);
  };

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
      const res = await api.asins({ q: effectiveQ, trash });
      if (res.asins.length < res.total) setNotice(`Selected the first ${res.asins.length} of ${res.total} matches (API limit).`);
      setSelected(new Set(res.asins));
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setGathering(false);
    }
  };

  const runAction = useCallback(async (label: string, asins: string[], run: (o: { signal: AbortSignal; onProgress: (d: number, t: number) => void }) => Promise<BulkSummary>) => {
    const ctl = new AbortController();
    abort.current = ctl;
    setBulk({ label, done: 0, total: asins.length, running: true, summary: null });
    const summary = await run({
      signal: ctl.signal,
      onProgress: (done, total) => setBulk((b) => (b ? { ...b, done, total } : b)),
    });
    setBulk({ label, done: asins.length, total: asins.length, running: false, summary });
    setSelected(new Set(notOk(summary)));
    setReload((n) => n + 1);
  }, []);

  const runBulkOp = (op: BulkOp) => {
    const asins = [...selected];
    switch (op.kind) {
      case "liberate":
        return runAction("Queueing", asins, (o) => api.bulkLiberate(asins, o));
      case "trash":
        return runAction(trash ? "Restoring" : "Trashing", asins, (o) => (trash ? api.bulkRestore(asins, o) : api.bulkTrash(asins, o)));
      case "purge":
        if (!window.confirm(`Permanently delete ${asins.length} ${asins.length === 1 ? "book" : "books"} from the trash? This cannot be undone.`)) return;
        return runAction("Deleting", asins, (o) => api.bulkPurge(asins, o));
      case "status":
        return runAction("Setting status", asins, (o) => api.bulkSetStatus(asins, op.status, o));
      case "pdfStatus":
        return runAction("Setting PDF status", asins, (o) => api.bulkSetPdfStatus(asins, op.status, o));
      case "tags":
        return runAction(op.mode === "add" ? "Adding tags" : "Removing tags", asins, (o) =>
          op.mode === "add" ? api.bulkAddTags(asins, op.tags, o) : api.bulkRemoveTags(asins, op.tags, o),
        );
    }
  };

  const applyQuery = (next: string) => {
    setTrash(false);
    setInput(next);
    resetView();
  };

  const sortBy = (key: SortKey) => {
    const next = toggleSort(sort, key);
    setSort(next);
    saveSort(next);
    setOffset(0);
  };

  const toggleCol = (id: ColumnId) => {
    const next = toggleColumn(visible, id);
    setVisible(next);
    saveVisible(next);
  };

  const pickSyntax = (text: string) => {
    setInput((cur) => `${cur.trim()} ${text}`.trim());
    resetView();
  };

  const rowAction = async (label: string, fn: () => Promise<unknown>) => {
    setNotice(null);
    try {
      await fn();
      setReload((n) => n + 1);
    } catch (e) {
      setNotice(`${label}: ${(e as Error).message}`);
    }
  };

  const setInputQuery = (next: string) => {
    setInput(next);
    resetView();
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
          className={`search ${error ? "invalid" : ""}`}
          placeholder='Filter, e.g. author:herbert -tags:skip title:"the hobbit"'
          value={input}
          onChange={(e) => {
            setInput(e.target.value);
            resetView();
          }}
          aria-label="Search library"
        />
        <SyntaxHelp onPick={pickSyntax} />
        {!trash && <QuickFilters current={input} onApply={applyQuery} />}
        <label className="check-label">
          <input
            type="checkbox"
            checked={hideEpisodes}
            onChange={(e) => {
              setHideEpisodes(e.target.checked);
              resetView();
            }}
          />
          Hide podcast episodes
        </label>
        <Popover label="Locate audiobooks" className="wide">{() => <LocatePanel />}</Popover>
        <Popover label="Columns">
          {() => (
            <div className="menu-list">
              {columns.map((c) => (
                <label key={c.id} className="check-label">
                  <input type="checkbox" checked={show(c.id)} onChange={() => toggleCol(c.id)} />
                  {c.label}
                </label>
              ))}
            </div>
          )}
        </Popover>
      </div>
      {!trash && <CountsBar refreshKey={reload} onApply={applyQuery} />}
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
          <BulkMenu count={selected.size} trash={trash} disabled={busy} onRun={runBulkOp} />
        </div>
      )}

      {bulk && (
        <div className="card">
          <div className="job-head">
            <span>
              {bulk.running ? `${bulk.label}...` : bulk.summary?.cancelled ? "Cancelled" : "Done"} {bulk.done} / {bulk.total}
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
          {bulk.summary && !bulk.running && <BulkReport s={bulk.summary} />}
        </div>
      )}

      <div className="tablewrap" aria-busy={loading}>
        <table>
          <thead>
            <tr>
              <th className="check">
                <input type="checkbox" checked={allOnPage} onChange={togglePage} disabled={items.length === 0 || busy} aria-label="Select page" />
              </th>
              <th className="cover-col" />
              <SortTh label="Title" sortKey="title" sort={sort} onSort={sortBy} />
              {columns.filter((c) => show(c.id)).map((c) => (
                <SortTh key={c.id} label={c.label} sortKey={c.sort} sort={sort} onSort={sortBy} className="hide-sm" />
              ))}
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((b) => (
              <tr key={b.asin} className={`clickable ${selected.has(b.asin) ? "selected" : ""} ${b.absentFromLastScan ? "absent" : ""}`} onClick={() => setDetail(b)}>
                <td className="check" onClick={(e) => e.stopPropagation()}>
                  <input type="checkbox" checked={selected.has(b.asin)} onChange={() => toggle(b.asin)} disabled={busy} aria-label={`Select ${b.title}`} />
                </td>
                <td className="cover-col">
                  <Cover asin={b.asin} pictureId={b.pictureId} size="small" />
                </td>
                <td>
                  <button
                    className="linklike title"
                    onClick={(e) => {
                      e.stopPropagation();
                      setDetail(b);
                    }}
                  >
                    {b.title}
                  </button>
                  {b.isAudiblePlus && <span className="pill plus" title={b.includedUntil ? `Included until ${formatDate(b.includedUntil)}` : "Audible Plus"}>Plus</span>}
                  {b.subtitle && <div className="muted small">{b.subtitle}</div>}
                  <div className="show-sm muted small">{[...b.authors, formatLength(b.lengthInMinutes)].join(" \u00b7 ")}</div>
                </td>
                {visible.map((id) => (
                  <td key={id} className={`hide-sm ${id === "length" || id === "purchased" || id === "lastDownload" || id === "myRating" || id === "rating" ? "nowrap" : ""}`}>
                    {cell(id, b, setInputQuery)}
                  </td>
                ))}
                <td className="actions" onClick={(e) => e.stopPropagation()}>
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
                <td colSpan={visible.length + 4} className="muted">
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
      {detail && (
        <BookDetails
          book={detail}
          onChange={patchRow}
          onClose={() => setDetail(null)}
          onSearch={(next) => {
            setDetail(null);
            setTrash(false);
            setInput(next);
            resetView();
          }}
        />
      )}
    </div>
  );
}

function BulkReport({ s }: { s: BulkSummary }) {
  const failed = s.failed.reduce((n, f) => n + f.asins.length, 0);
  const parts = [`${s.updated} updated`];
  if (s.conflict.length) parts.push(`${s.conflict.length} conflict`);
  if (s.notFound.length) parts.push(`${s.notFound.length} not found`);
  if (failed) parts.push(`${failed} failed`);
  const bad = s.conflict.length + s.notFound.length + failed > 0;
  return (
    <>
      <p className={bad ? "error" : "muted"}>
        {parts.join(", ")}
        {bad && " (still selected)"}
      </p>
      {s.failed.length > 0 && (
        <ul className="plain small">
          {[...new Set(s.failed.map((f) => f.error))].map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}
    </>
  );
}

function SortTh({ label, sortKey, sort, onSort, className }: { label: string; sortKey?: SortKey; sort: SortState | null; onSort: (k: SortKey) => void; className?: string }) {
  if (!sortKey) return <th className={className}>{label}</th>;
  const active = sort?.key === sortKey;
  return (
    <th className={className} aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : "none"}>
      <button className="sort" onClick={() => onSort(sortKey)} title={`Sort by ${label.toLowerCase()}`}>
        {label}
        {active && <span aria-hidden="true">{sort.dir === "asc" ? " \u25b2" : " \u25bc"}</span>}
      </button>
    </th>
  );
}

function cell(id: ColumnId, b: Book, search: (q: string) => void) {
  switch (id) {
    case "authors":
      return b.authors.join(", ");
    case "narrators":
      return b.narrators.join(", ");
    case "series":
      return b.series.map((s, i) => (
        <span key={s.asin}>
          {i > 0 && ", "}
          <button className="linklike" title="Search this series" onClick={(e) => { e.stopPropagation(); search(seriesQuery(s.asin)); }}>
            {s.order ? `${s.name} #${s.order}` : s.name}
          </button>
        </span>
      ));
    case "length":
      return formatLength(b.lengthInMinutes);
    case "status":
      return (
        <>
          <span className={`pill book-${b.bookStatus}`}>{b.bookStatus}</span>
          {b.absentFromLastScan && <span className="pill warn" title="Absent from the last scan">Unavailable</span>}
          {b.pdfStatus && <span className={`pill book-${b.pdfStatus}`} title="PDF status">PDF</span>}
        </>
      );
    case "myRating":
      return <span title={b.myRating.overall ? String(b.myRating.overall) : "not rated"}>{stars(b.myRating.overall)}</span>;
    case "rating":
      return <span title={b.productRating.overall ? String(b.productRating.overall) : "not rated"}>{stars(b.productRating.overall)}</span>;
    case "category":
      return b.categories.join(", ");
    case "purchased":
      return formatDate(b.dateAdded);
    case "lastDownload":
      return formatDate(b.lastDownloaded);
    case "tags":
      return b.tags;
  }
}
