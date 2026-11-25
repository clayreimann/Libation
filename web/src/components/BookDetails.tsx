import { useEffect, useState } from "react";
import { api, ApiError, type LiberatedChoice } from "../api/client";
import type { Book, BookDetail } from "../api/types";
import { descriptionText } from "../description";
import { audibleUrl, seriesQuery } from "../filters";
import { formatDate, formatLength, formatTime, humanize, stars } from "../format";
import { normalizeTags } from "../tags";
import { Cover } from "./Cover";

interface Props {
  book: Book;
  onChange: (b: Book) => void;
  onClose: () => void;
  onSearch: (q: string) => void;
}

const statusValue = (s: string) => (s === "Liberated" ? "liberated" : s === "NotLiberated" ? "notLiberated" : "error");

function ratingText(r: { overall: number; performance: number; story: number }): string {
  return r.overall > 0 ? `${stars(r.overall)} ${r.overall.toFixed(1)} (performance ${r.performance.toFixed(1)}, story ${r.story.toFixed(1)})` : "none";
}

export function BookDetails({ book, onChange, onClose, onSearch }: Props) {
  const [detail, setDetail] = useState<BookDetail | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [tags, setTags] = useState(book.tags);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ text: string; bad: boolean } | null>(null);

  useEffect(() => {
    setTags(book.tags);
    setMsg(null);
  }, [book.asin]);

  useEffect(() => {
    const ctl = new AbortController();
    setDetailError(null);
    api.getBook(book.asin, ctl.signal).then(setDetail, (e: Error) => !ctl.signal.aborted && setDetailError(e.message));
    return () => ctl.abort();
  }, [book.asin, refresh]);

  useEffect(() => {
    setDetail(null);
  }, [book.asin]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const run = async (label: string, fn: () => Promise<Book | unknown>, done?: string) => {
    setBusy(true);
    setMsg(null);
    try {
      const res = await fn();
      if (res && typeof res === "object" && "asin" in res) {
        const b = res as Book;
        onChange(b);
        setTags(b.tags);
        setRefresh((n) => n + 1);
      }
      if (done) setMsg({ text: done, bad: false });
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) setMsg({ text: "Already queued.", bad: false });
      else setMsg({ text: `${label}: ${(e as Error).message}`, bad: true });
    } finally {
      setBusy(false);
    }
  };

  const normalized = normalizeTags(tags).join(" ");
  const dirty = normalized !== book.tags;
  const d = detail?.asin === book.asin ? detail : null;
  const description = descriptionText(d?.description);

  const seriesLinks = book.series.length ? (
    <ul className="plain">
      {book.series.map((s) => (
        <li key={s.asin}>
          <button className="linklike" title="Search this series" onClick={() => onSearch(seriesQuery(s.asin))}>
            {s.order ? `${s.name} #${s.order}` : s.name}
          </button>
        </li>
      ))}
    </ul>
  ) : (
    "none"
  );

  const rows: [string, React.ReactNode][] = [
    ["Authors", book.authors.join(", ") || "none"],
    ["Narrators", book.narrators.join(", ") || "none"],
    ["Series", seriesLinks],
    ["Length", formatLength(book.lengthInMinutes)],
    ["Publisher", d?.publisher || "unknown"],
    ["Language", d?.language || "unknown"],
    ["Published", formatDate(d?.datePublished) || "unknown"],
    ["Abridged", d ? (d.isAbridged ? "yes" : "no") : "..."],
    ["Date added", formatTime(book.dateAdded)],
    ["Account", book.account],
    ["Locale", book.locale],
    ["Content type", humanize(book.contentType)],
    ["Podcast episode", book.isPodcastEpisode ? "yes" : "no"],
    ["Audible Plus", book.isAudiblePlus ? (book.includedUntil ? `yes, until ${formatDate(book.includedUntil)}` : "yes") : "no"],
    ["Spatial audio", book.isSpatial ? "yes" : "no"],
    ["Rating", ratingText(book.productRating)],
    ["My rating", ratingText(book.myRating)],
    ["Book status", <span className={`pill book-${book.bookStatus}`}>{book.bookStatus}</span>],
    ["Availability", book.absentFromLastScan ? <span className="pill warn">Unavailable: absent from the last scan</span> : "in library"],
    ["PDF status", book.pdfStatus ? <span className={`pill book-${book.pdfStatus}`}>{book.pdfStatus}</span> : "no PDF"],
    [
      "Last download",
      book.lastDownloaded
        ? [formatTime(book.lastDownloaded), book.lastDownloadedVersion && `version ${book.lastDownloadedVersion}`, book.lastDownloadedFormat].filter(Boolean).join(", ")
        : "never",
    ],
    ["Audio file on disk", d ? (d.audioFileExists ? "yes" : "no") : "..."],
    ["Partial download", d ? (d.partialDownload ? <span className="pill warn">partial file present</span> : "no") : "..."],
    [
      "ASIN",
      <a href={audibleUrl(book.locale, book.asin)} target="_blank" rel="noreferrer noopener">
        {book.asin}
      </a>,
    ],
  ];

  return (
    <>
      <div className="backdrop" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-label={`Details for ${book.title}`}>
        <div className="drawer-head">
          <div>
            <h2>{book.title}</h2>
            {book.subtitle && <div className="muted">{book.subtitle}</div>}
          </div>
          <button onClick={onClose} aria-label="Close details">
            Close
          </button>
        </div>

        <Cover asin={book.asin} pictureId={book.pictureId} size="large" />
        {book.parentAsin && (
          <p>
            <button onClick={() => onSearch(seriesQuery(book.parentAsin!))}>Show episodes of this podcast</button>
          </p>
        )}
        {detailError && <p className="error">Details unavailable: {detailError}</p>}
        {description && <p className="description">{description}</p>}

        <dl className="drawer-dl">
          {rows.map(([k, v]) => (
            <div key={k}>
              <dt>{k}</dt>
              <dd>{v}</dd>
            </div>
          ))}
        </dl>

        {(d?.categoryLadders.length ?? 0) > 0 && (
          <>
            <h3>Categories</h3>
            <ul className="plain">
              {d!.categoryLadders.map((l) => (
                <li key={l.join(">")}>{l.join(" \u203a ")}</li>
              ))}
            </ul>
          </>
        )}

        <h3>Tags</h3>
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault();
            if (dirty) void run("Save tags", () => api.setTags(book.asin, normalized), "Tags saved.");
          }}
        >
          <input
            value={tags}
            onChange={(e) => setTags(e.target.value)}
            placeholder="space separated; letters, numbers, underscores"
            aria-label="Tags"
            disabled={busy}
          />
          <button type="submit" className="primary" disabled={busy || !dirty}>
            Save
          </button>
        </form>
        {dirty && <p className="muted small">Will save as: {normalized || "(no tags)"}</p>}

        <h3>Status</h3>
        <select
          value={statusValue(book.bookStatus)}
          disabled={busy}
          aria-label="Book status"
          onChange={(e) => void run("Set status", () => api.setStatus(book.asin, e.target.value as LiberatedChoice))}
        >
          <option value="liberated">Liberated</option>
          <option value="notLiberated">Not liberated</option>
          {book.bookStatus === "Error" && (
            <option value="error" disabled>
              Error
            </option>
          )}
        </select>

        <div className="row drawer-actions">
          {!book.inTrash && (
            <button onClick={() => void run("Liberate", () => api.liberateBook(book.asin), "Queued for liberation.")} disabled={busy}>
              Liberate
            </button>
          )}
          <button onClick={() => void run(book.inTrash ? "Restore" : "Trash", () => (book.inTrash ? api.restore(book.asin) : api.trash(book.asin)))} disabled={busy}>
            {book.inTrash ? "Restore" : "Move to trash"}
          </button>
        </div>
        {msg && <p className={msg.bad ? "error" : "muted"}>{msg.text}</p>}
      </aside>
    </>
  );
}
