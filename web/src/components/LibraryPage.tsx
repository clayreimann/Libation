import { useCallback, useEffect, useRef, useState } from "react";
import { api, type ExportFormat } from "../api/client";
import type { Book, LibraryPage as Page, Stats } from "../api/types";
import { notOk, type BulkSummary } from "../bulk";
import { columns, loadVisible, saveVisible, toggleColumn, type ColumnId } from "../columns";
import { countQueries, seriesQuery, withHideEpisodes } from "../filters";
import { formatDate, formatLength, formatTime, humanize, stars } from "../format";
import { groupPodcasts, needsParents, type ParentRow } from "../podcasts";
import { defaultQuery } from "../quickFilters";
import type { useQuickFilters } from "../useQuickFilters";
import { readPref, writePref } from "../prefs";
import { scopedQuery, scopeLabel, scopes, routeHref, type Scope } from "../route";
import { useMedia } from "../useMedia";
import { Icon } from "./Icon";
import { QueueMini } from "./QueueMini";
import { StatusLamp } from "./StatusLamp";
import { seriesSort } from "../series";
import { loadSort, saveSort, toggleSort, type SortKey, type SortState } from "../sort";
import { useLive } from "../live";
import { BookDetails } from "./BookDetails";
import { usePlayer } from "./Player";
import { BulkMenu, type BulkOp } from "./BulkMenu";
import { Cover } from "./Cover";
import { InactivePanel } from "./InactivePanel";
import { RemoveFromAudible } from "./RemoveFromAudible";
import { LocatePanel } from "./LocatePanel";
import { Popover } from "./Popover";
import { SeriesBrowser } from "./SeriesBrowser";
import { QualityPanel } from "./QualityPanel";
import { SyntaxHelp } from "./SyntaxHelp";

const PAGE_SIZE = 50;
const exportFormats: { id: ExportFormat; label: string }[] = [
  { id: "csv", label: "CSV" },
  { id: "xlsx", label: "Excel (XLSX)" },
  { id: "json", label: "JSON" },
];

let defaultFilterHandled = false;

interface BulkState {
  label: string;
  done: number;
  total: number;
  running: boolean;
  summary: BulkSummary | null;
  okWord?: string;
}


function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

const INSPECTOR_KEY = "libation.inspector";

interface Props {
  scope: Scope;
  input: string;
  setInput: (q: string) => void;
  qf: ReturnType<typeof useQuickFilters>;
  stats: Stats | null;
  onChanged: () => void;
  onToggleSidebar: () => void;
}

export function LibraryPage({ scope, input, setInput, qf, stats, onChanged, onToggleSidebar }: Props) {
  const player = usePlayer();
  const { onBookEvent, status, setStatus } = useLive();
  const q = useDebounced(input, 300);
  const wide = useMedia("(min-width: 1200px)");
  const phone = useMedia("(max-width: 720px)");
  const [inspector, setInspector] = useState(() => readPref(INSPECTOR_KEY) !== false);
  const [inspectorTab, setInspectorTab] = useState<"details" | "queue">("details");
  const [parents, setParents] = useState<Book[]>([]);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const trash = scope === "trash";
  const [offset, setOffset] = useState(0);
  const [page, setPage] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [reload, setReload] = useState(0);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [selecting, setSelecting] = useState(false);
  const [bulk, setBulk] = useState<BulkState | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [gathering, setGathering] = useState(false);
  const [hideEpisodes, setHideEpisodes] = useState(false);
  const [removing, setRemoving] = useState<string[] | null>(null);
  const [detail, setDetail] = useState<Book | null>(null);
  const [userSort, setSort] = useState<SortState | null>(loadSort);
  const [visible, setVisible] = useState<ColumnId[]>(loadVisible);
  const [scanMsg, setScanMsg] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);

  const sort = scope === "series" ? seriesSort : userSort;
  const effectiveQ = withHideEpisodes(scopedQuery(scope, q), hideEpisodes && scope !== "podcasts");
  const panel = wide && inspector;

  useEffect(() => {
    if (defaultFilterHandled || !qf.saved) return;
    defaultFilterHandled = true;
    const dq = defaultQuery(qf.saved);
    if (dq && !input) setInput(dq);
  }, [qf.saved]); // eslint-disable-line react-hooks/exhaustive-deps
  const show = (id: ColumnId) => visible.includes(id);

  useEffect(() => {
    setOffset(0);
    setSelected(new Set());
  }, [scope]);

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
  }, [offset, effectiveQ, trash, sort?.key, sort?.dir, reload]); // eslint-disable-line react-hooks/exhaustive-deps

  const items = page?.items ?? [];
  const hasEpisodes = needsParents(items);
  useEffect(() => {
    if (!hasEpisodes) {
      setParents([]);
      return;
    }
    const ctl = new AbortController();
    api.podcasts({ q: effectiveQ, trash }, ctl.signal).then(setParents, () => undefined);
    return () => ctl.abort();
  }, [hasEpisodes, effectiveQ, trash, reload]);

  const bump = () => {
    setReload((n) => n + 1);
    onChanged();
  };

  const patchRow = (b: Book) => {
    setDetail((d) => (d?.asin === b.asin ? b : d));
    setPage((p) => (p ? { ...p, items: p.items.map((i) => (i.asin === b.asin ? b : i)) } : p));
    bump();
  };

  useEffect(() => onBookEvent(() => setReload((n) => n + 1)), [onBookEvent]);

  const resetView = () => {
    setOffset(0);
    setSelected(new Set());
  };

  const rows = groupPodcasts(items, parents);
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

  const runAction = useCallback(async (label: string, asins: string[], run: (o: { signal: AbortSignal; onProgress: (d: number, t: number) => void }) => Promise<BulkSummary>, okWord?: string) => {
    const ctl = new AbortController();
    abort.current = ctl;
    setBulk({ label, done: 0, total: asins.length, running: true, summary: null, okWord });
    const summary = await run({
      signal: ctl.signal,
      onProgress: (done, total) => setBulk((b) => (b ? { ...b, done, total } : b)),
    });
    setBulk({ label, done: asins.length, total: asins.length, running: false, summary, okWord });
    setSelected(new Set(notOk(summary)));
    setReload((n) => n + 1);
    onChanged();
  }, [onChanged]);

  const runBulkOp = (op: BulkOp) => {
    const asins = [...selected];
    switch (op.kind) {
      case "liberate":
        return runAction("Queueing", asins, (o) => api.bulkLiberate(asins, o));
      case "absUpload":
        return runAction("Queueing Audiobookshelf uploads", asins, (o) => api.bulkAbsUpload(asins, o));
      case "removeAudible":
        return setRemoving(asins);
      case "pushProgress":
        return runAction("Queueing progress pushes", asins, (o) => api.bulkPushProgress(asins, op.force, o), "queued");
      case "convertMp3":
        return runAction("Queueing MP3 conversions", asins, (o) => api.bulkConvertMp3(asins, o));
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

  /** "Liberate N": every book that is not downloaded, like the desktop button. */
  const liberateAll = async () => {
    setNotice(null);
    try {
      const res = await api.asins({ q: countQueries.notLiberated, trash: false });
      if (res.asins.length === 0) return setNotice("Nothing left to liberate.");
      await runAction("Queueing", res.asins, (o) => api.bulkLiberate(res.asins, o), "queued");
    } catch (e) {
      setNotice((e as Error).message);
    }
  };

  const scanRun = async (kind: "scan" | "liberate") => {
    setScanMsg(null);
    try {
      setStatus(await (kind === "scan" ? api.scan() : api.liberate()));
      setScanMsg(kind === "scan" ? "Scan requested." : "Scan and liberate requested.");
    } catch (e) {
      setScanMsg((e as Error).message);
    }
  };

  const setInputQuery = (next: string) => {
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

  const showSeries = (id: string, next: SortState = seriesSort) => {
    if (scope !== "all") window.location.hash = routeHref({ scope: "all" });
    setInput(seriesQuery(id));
    setSort(next);
    resetView();
  };

  const pickSyntax = (text: string) => {
    setInput(`${input.trim()} ${text}`.trim());
    resetView();
  };

  const rowAction = async (label: string, fn: () => Promise<unknown>) => {
    setNotice(null);
    try {
      await fn();
      bump();
    } catch (e) {
      setNotice(`${label}: ${(e as Error).message}`);
    }
  };

  const toggleInspector = () =>
    setInspector((o) => {
      writePref(INSPECTOR_KEY, !o);
      return !o;
    });

  const busy = bulk?.running === true;
  const end = Math.min(offset + PAGE_SIZE, total);
  const colSpan = visible.length + 5;
  const showChecks = !phone || selecting || selected.size > 0;

  const open = (b: Book) => {
    setDetail(b);
    setInspectorTab("details");
  };

  const bookRow = (b: Book, nested = false) => {
    const isSel = selected.has(b.asin);
    return (
      <tr
        key={b.asin}
        className={`bookrow clickable ${nested ? "episode" : ""} ${isSel ? "selected" : ""} ${detail?.asin === b.asin ? "focused" : ""} ${b.absentFromLastScan ? "absent" : ""}`}
        onClick={() => (phone && selecting ? toggle(b.asin) : open(b))}
      >
        <td className="check" onClick={(e) => e.stopPropagation()}>
          <input type="checkbox" checked={isSel} onChange={() => toggle(b.asin)} disabled={busy} aria-label={`Select ${b.title}`} />
        </td>
        <td className="lamp-col">
          <StatusLamp book={b} disabled={busy} onLiberate={() => void rowAction("Liberate", () => api.liberateBook(b.asin))} />
        </td>
        <td className="cover-col">
          <Cover asin={b.asin} pictureId={b.pictureId} size="small" />
        </td>
        <td className="title-col">
          <div className="title">{b.title}</div>
          {b.subtitle && <div className="sub">{b.subtitle}</div>}
          <div className="row-meta">
            {[b.authors.join(", "), formatLength(b.lengthInMinutes), b.series[0] ? (b.series[0].order ? `${b.series[0].name} #${b.series[0].order}` : b.series[0].name) : ""].filter(Boolean).join(" · ")}
          </div>
        </td>
        {visible.map((id) => (
          <td key={id} className={`col col-${id} ${id === "length" || id === "purchased" || id === "lastDownload" || id === "myRating" || id === "rating" ? "nowrap" : ""}`}>
            {cell(id, b, setInputQuery)}
          </td>
        ))}
        <td className="actions" onClick={(e) => e.stopPropagation()}>
          {b.bookStatus === "Liberated" && !b.inTrash && (
            <button className="icon-btn play-btn" onClick={() => player.play(b)} title="Play" aria-label={`Play ${b.title}`}>
              <Icon name="play" size={14} />
            </button>
          )}
        </td>
      </tr>
    );
  };

  const toggleExpanded = (asin: string) =>
    setExpanded((s) => {
      const n = new Set(s);
      if (!n.delete(asin)) n.add(asin);
      return n;
    });

  const parentRows = ({ parent, episodes }: ParentRow) => {
    const isOpen = expanded.has(parent.asin);
    const asins = episodes.map((e) => e.asin);
    const all = asins.every((a) => selected.has(a));
    return [
      <tr key={parent.asin} className="bookrow clickable parent" onClick={() => toggleExpanded(parent.asin)}>
        <td className="check" onClick={(e) => e.stopPropagation()}>
          <input
            type="checkbox"
            checked={all}
            onChange={() =>
              setSelected((s) => {
                const n = new Set(s);
                for (const a of asins) all ? n.delete(a) : n.add(a);
                return n;
              })
            }
            disabled={busy}
            aria-label={`Select episodes of ${parent.title}`}
          />
        </td>
        <td className="lamp-col">
          <span className="disclosure" aria-hidden="true">
            <Icon name={isOpen ? "chevronDown" : "chevronRight"} size={14} />
          </span>
        </td>
        <td className="cover-col">
          <Cover asin={parent.asin} pictureId={parent.pictureId} size="small" />
        </td>
        <td className="title-col">
          <button className="linklike title" aria-expanded={isOpen} onClick={(e) => { e.stopPropagation(); toggleExpanded(parent.asin); }}>
            {parent.title}
          </button>
          <div className="row-meta">
            {episodes.length} {episodes.length === 1 ? "episode" : "episodes"}
            {parent.authors.length > 0 && ` · ${parent.authors.join(", ")}`}
          </div>
        </td>
        {visible.map((id) => (
          <td key={id} className={`col col-${id}`}>
            {id === "authors" ? parent.authors.join(", ") : id === "narrators" ? parent.narrators.join(", ") : id === "category" ? parent.categories.join(", ") : null}
          </td>
        ))}
        <td className="actions" />
      </tr>,
      ...(isOpen ? episodes.map((e) => bookRow(e, true)) : []),
    ];
  };

  /** Series scope: a header row whenever the first series changes, like the desktop's series sections. */
  const body: React.ReactNode[] = [];
  let lastSeries: string | null = null;
  for (const r of rows) {
    if (scope === "series" && r.kind === "book") {
      const name = r.book.series[0]?.name ?? "";
      if (name !== lastSeries) {
        lastSeries = name;
        body.push(
          <tr key={`series:${name}:${r.book.asin}`} className="section-row">
            <td colSpan={colSpan}>{name || "No series"}</td>
          </tr>,
        );
      }
    }
    body.push(r.kind === "book" ? bookRow(r.book) : parentRows(r));
  }

  const notLiberated = stats?.notLiberated ?? 0;
  const searchPlaceholder = phone ? "Search books" : "Search title, author, narrator…  try author:austen";

  const moreMenu = (
    <Popover label={<Icon name="more" />} className="wide icon-pop" title="More actions">
      {(close) => (
        <div className="menu">
          <label className="menu-check">
            <input
              type="checkbox"
              checked={hideEpisodes}
              disabled={scope === "podcasts"}
              onChange={(e) => {
                setHideEpisodes(e.target.checked);
                resetView();
              }}
            />
            Hide podcast episodes
          </label>
          <div className="menu-sep" />
          <div className="menu-label">Export {trash ? "trash" : "these books"}</div>
          {exportFormats.map((f) =>
            error ? (
              <span key={f.id} className="menu-item muted">{f.label}</span>
            ) : (
              <a key={f.id} className="menu-item" href={api.exportUrl(f.id, { q: effectiveQ, trash })} download onClick={() => window.setTimeout(close)}>
                {f.label}
              </a>
            ),
          )}
          {!trash && (
            <>
              <div className="menu-sep" />
              <Popover label="Browse series…" className="wide sub">{(c2) => <SeriesBrowser onPick={(id) => { c2(); close(); showSeries(id); }} />}</Popover>
              <Popover label="Find better quality…" className="wide sub">{() => <QualityPanel />}</Popover>
              <Popover label="Scan for removed books…" className="wide sub">{() => <InactivePanel onTrashed={bump} />}</Popover>
            </>
          )}
          <Popover label="Locate audiobooks…" className="wide sub">{() => <LocatePanel />}</Popover>
          {!phone && (
            <>
              <div className="menu-sep" />
              <div className="menu-label">Columns</div>
              {columns.map((c) => (
                <label key={c.id} className="menu-check">
                  <input type="checkbox" checked={show(c.id)} onChange={() => toggleCol(c.id)} />
                  {c.label}
                </label>
              ))}
            </>
          )}
        </div>
      )}
    </Popover>
  );

  const detailsView = detail && (
    <BookDetails
      book={detail}
      variant={panel ? "panel" : phone ? "sheet" : "drawer"}
      onChange={patchRow}
      onClose={() => setDetail(null)}
      onSearch={(next, nextSort) => {
        if (!panel) setDetail(null);
        if (scope !== "all") window.location.hash = routeHref({ scope: "all" });
        setInput(next);
        if (nextSort) setSort(nextSort);
        resetView();
      }}
    />
  );

  return (
    <div className={`library ${panel ? "with-inspector" : ""} ${showChecks ? "" : "no-checks"}`}>
      <div className="library-main">
        <div className="toolbar">
          <button className="icon-btn" onClick={onToggleSidebar} aria-label="Toggle sidebar" title="Toggle sidebar">
            <Icon name={phone ? "menu" : "sidebar"} />
          </button>
          {phone && <h1 className="phone-title">{scope === "all" ? "Library" : scopeLabel(scope)}</h1>}
          <div className={`searchbox ${error ? "invalid" : ""}`}>
            <Icon name="search" size={16} />
            <input
              type="search"
              placeholder={searchPlaceholder}
              value={input}
              onChange={(e) => {
                setInput(e.target.value);
                resetView();
              }}
              aria-label="Search library"
            />
            {input && (
              <button className="clear" onClick={() => setInputQuery("")} aria-label="Clear search">
                <Icon name="close" size={14} />
              </button>
            )}
          </div>
          <SyntaxHelp onPick={pickSyntax} />
          {!trash && (
            <div className="tool-actions">
              <button className="btn" onClick={() => void scanRun("scan")} title="Check every account for new books">
                <Icon name="refresh" size={16} className="show-phone" />
                <span className="hide-phone">Scan library</span>
              </button>
              <div className="split">
                <button className="btn primary" onClick={() => void liberateAll()} disabled={busy || notLiberated === 0} title="Download every book that is not downloaded yet">
                  Liberate{notLiberated > 0 ? ` ${notLiberated}` : ""}
                </button>
                <Popover label={<Icon name="chevronDown" size={14} />} className="split-pop" title="More liberate options">
                  {(close) => (
                    <div className="menu">
                      <button className="menu-item" onClick={() => { close(); void scanRun("liberate"); }}>
                        Scan, then liberate new books
                      </button>
                      {selected.size > 0 && (
                        <button className="menu-item" onClick={() => { close(); runBulkOp({ kind: "liberate" }); }}>
                          Liberate {selected.size} selected
                        </button>
                      )}
                    </div>
                  )}
                </Popover>
              </div>
            </div>
          )}
          {moreMenu}
          {wide && (
            <button className={`icon-btn ${inspector ? "on" : ""}`} onClick={toggleInspector} aria-label="Toggle inspector" aria-pressed={inspector} title="Toggle inspector">
              <Icon name="inspector" />
            </button>
          )}
        </div>

        <div className="chips-bar" role="navigation" aria-label="Library views">
          {scopes.filter((s) => s.id !== "trash").map((s) => (
            <a key={s.id} href={routeHref({ scope: s.id })} className={`scope-chip ${scope === s.id ? "current" : ""}`}>
              {s.label}
              {s.id === "not-downloaded" && stats ? <span className="chip-count">{stats.notLiberated}</span> : null}
              {s.id === "needs-attention" && stats && stats.error > 0 ? <span className="chip-count bad">{stats.error}</span> : null}
            </a>
          ))}
          {qf.filters.map((f) => (
            <button key={f.filter} className={`scope-chip filter ${input.trim() === f.filter ? "current" : ""}`} onClick={() => setInputQuery(input.trim() === f.filter ? "" : f.filter)} title={f.filter}>
              <Icon name="filter" size={12} /> {f.name?.trim() || f.filter}
            </button>
          ))}
          <a href={routeHref({ scope: "trash" })} className={`scope-chip ${trash ? "current" : ""}`}>Trash</a>
        </div>

        {(scope !== "all" || scanMsg || notice || error) && (
          <div className="infobar">
            {scope !== "all" && <span className="scope-title">{scopeLabel(scope)}</span>}
            {error && <span className="error">{error}</span>}
            {notice && <span className="error">{notice}</span>}
            {scanMsg && <span className="muted">{scanMsg}</span>}
          </div>
        )}

        {(selected.size > 0 || (phone && selecting)) && (
          <div className="bulkbar">
            <strong>{selected.size} selected</strong>
            {selected.size < total && !busy && (
              <button className="btn subtle" onClick={selectAllMatching} disabled={gathering}>
                {gathering ? "Gathering..." : phone ? `All ${total}` : `Select all ${total}`}
              </button>
            )}
            <span className="spacer" />
            <BulkMenu count={selected.size} trash={trash} disabled={busy || selected.size === 0} onRun={runBulkOp} />
            <button className="btn subtle" onClick={() => { setSelected(new Set()); setSelecting(false); }} disabled={busy}>
              Done
            </button>
          </div>
        )}

        {removing && (
          <RemoveFromAudible
            selected={removing}
            titles={new Map(items.map((b) => [b.asin, b.title]))}
            onClose={() => setRemoving(null)}
            onRemoved={(asins) => {
              const gone = new Set(asins);
              setSelected((s) => new Set([...s].filter((a) => !gone.has(a))));
              setDetail((d) => (d && gone.has(d.asin) ? null : d));
              bump();
            }}
          />
        )}

        {bulk && (
          <div className="card bulk-card">
            <div className="job-head">
              <span>
                {bulk.running ? `${bulk.label}...` : bulk.summary?.cancelled ? "Cancelled" : "Done"} {bulk.done} / {bulk.total}
              </span>
              {bulk.running ? (
                <button className="btn" onClick={() => abort.current?.abort()}>Cancel</button>
              ) : (
                <button className="btn" onClick={() => setBulk(null)}>Dismiss</button>
              )}
            </div>
            <div className="bar thin">
              <div style={{ width: `${bulk.total ? (bulk.done / bulk.total) * 100 : 0}%` }} />
            </div>
            {bulk.summary && !bulk.running && <BulkReport s={bulk.summary} okWord={bulk.okWord} />}
            {bulk.summary && !bulk.running && bulk.okWord === "queued" && (
              <p className="muted small">
                Outcomes are in each job's log on the <a href="#/queue">queue</a>.
              </p>
            )}
          </div>
        )}

        <div className="tablewrap" aria-busy={loading}>
          <table className="books">
            <thead>
              <tr>
                <th className="check">
                  <input type="checkbox" checked={allOnPage} onChange={togglePage} disabled={items.length === 0 || busy} aria-label="Select page" />
                </th>
                <th className="lamp-col">Liberate</th>
                <th className="cover-col">Cover</th>
                <SortTh label="Title" sortKey="title" sort={sort} onSort={sortBy} className="title-col" />
                {columns.filter((c) => show(c.id)).map((c) => (
                  <SortTh key={c.id} label={c.label} sortKey={c.sort} sort={sort} onSort={sortBy} className={`col col-${c.id}`} />
                ))}
                <th className="actions" />
              </tr>
            </thead>
            <tbody>
              {body}
              {page && items.length === 0 && (
                <tr className="empty-row">
                  <td colSpan={colSpan}>
                    <div className="empty">
                      <Icon name={trash ? "trash" : "search"} size={36} />
                      <strong>{trash ? "The trash is empty" : input.trim() ? "No books match" : `Nothing in ${scopeLabel(scope)}`}</strong>
                      {!trash && input.trim() && (
                        <button className="btn" onClick={() => setInputQuery("")}>
                          Clear search
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

        <footer className="statusbar">
          <span className="sb-left">
            <span>{total === 0 ? "0 books" : total > PAGE_SIZE ? `${offset + 1}–${end} of ${total}` : `${total} ${total === 1 ? "book" : "books"}`}</span>
            {selected.size > 0 && <span className="muted"> · {selected.size} selected</span>}
            {phone && !selecting && selected.size === 0 && items.length > 0 && (
              <button className="linklike sb-select" onClick={() => setSelecting(true)}>
                Select
              </button>
            )}
          </span>
          {total > PAGE_SIZE && (
            <span className="pager">
              <button className="icon-btn small" onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))} disabled={offset === 0} aria-label="Previous page">
                <Icon name="chevronLeft" size={14} />
              </button>
              <button className="icon-btn small" onClick={() => setOffset(offset + PAGE_SIZE)} disabled={offset + PAGE_SIZE >= total} aria-label="Next page">
                <Icon name="chevronRight" size={14} />
              </button>
            </span>
          )}
          {stats && (
            <span className="sb-counts">
              <a href={routeHref({ scope: "downloaded" })}><i className="dot ok" />{stats.liberated} downloaded</a>
              <a href={routeHref({ scope: "not-downloaded" })}><i className="dot accent" />{stats.notLiberated} to do</a>
              {stats.partial > 0 && <span><i className="dot warn" />{stats.partial} partial</span>}
              {stats.error > 0 && <a href={routeHref({ scope: "needs-attention" })}><i className="dot bad" />{stats.error} {stats.error === 1 ? "error" : "errors"}</a>}
              {stats.unavailable > 0 && <a href={routeHref({ scope: "unavailable" })}><i className="dot gray" />{stats.unavailable} unavailable</a>}
            </span>
          )}
          {status && !phone && (
            <span className="sb-run muted" title={`Last run ${formatTime(status.lastRun)}`}>
              {humanize(status.state)}
            </span>
          )}
        </footer>
      </div>

      {panel && (
        <aside className="inspector" aria-label="Inspector">
          <div className="seg-tabs" role="tablist">
            <button role="tab" aria-selected={inspectorTab === "details"} className={inspectorTab === "details" ? "active" : ""} onClick={() => setInspectorTab("details")}>
              Details
            </button>
            <button role="tab" aria-selected={inspectorTab === "queue"} className={inspectorTab === "queue" ? "active" : ""} onClick={() => setInspectorTab("queue")}>
              Queue
            </button>
          </div>
          <div className="inspector-body">
            {inspectorTab === "queue" ? (
              <QueueMini />
            ) : detailsView ? (
              detailsView
            ) : (
              <div className="empty">
                <Icon name="library" size={36} />
                <strong>No book selected</strong>
                <span>Select a book to see its details.</span>
              </div>
            )}
          </div>
        </aside>
      )}
      {!panel && detailsView}
    </div>
  );
}

function BulkReport({ s, okWord = "updated" }: { s: BulkSummary; okWord?: string }) {
  const failed = s.failed.reduce((n, f) => n + f.asins.length, 0);
  const parts = [`${s.updated} ${okWord}`];
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
          {b.partialDownload && <span className="pill warn" title="Partly downloaded file on disk">Partial</span>}
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
