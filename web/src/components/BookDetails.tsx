import { useEffect, useState } from "react";
import { api, ApiError, type LiberatedChoice } from "../api/client";
import type { AudioFile, Book, BookDetail } from "../api/types";
import { descriptionText } from "../description";
import { audibleUrl, seriesQuery } from "../filters";
import { formatDate, formatLength, formatTime, humanize, stars } from "../format";
import { normalizeTags } from "../tags";
import { booksFolder } from "../booksFolder";
import { canConvertToMp3, convertOutcome } from "../convert";
import { canRate } from "../rating";
import { seriesSort } from "../series";
import type { SortState } from "../sort";
import { BookmarksExport } from "./BookmarksExport";
import { ProgressPanel } from "./ProgressPanel";
import { RatingEditor } from "./RatingEditor";
import { SeriesPanel } from "./SeriesPanel";
import { Cover } from "./Cover";
import { usePlayer } from "./Player";
import { Icon } from "./Icon";
import { lampLabel, lampState } from "./StatusLamp";

interface Props {
  book: Book;
  variant?: "panel" | "drawer" | "sheet";
  onChange: (b: Book) => void;
  onClose: () => void;
  onSearch: (q: string, sort?: SortState) => void;
}

const statusValue = (s: string) => (s === "Liberated" ? "liberated" : s === "NotLiberated" ? "notLiberated" : "error");

function ratingText(r: { overall: number; performance: number; story: number }): string {
  return r.overall > 0 ? `${stars(r.overall)} ${r.overall.toFixed(1)} (performance ${r.performance.toFixed(1)}, story ${r.story.toFixed(1)})` : "none";
}

export function BookDetails({ book, variant = "drawer", onChange, onClose, onSearch }: Props) {
  const player = usePlayer();
  const [detail, setDetail] = useState<BookDetail | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [books, setBooks] = useState("");
  const [locatePath, setLocatePath] = useState("");
  const [files, setFiles] = useState<AudioFile[]>([]);

  useEffect(() => {
    void booksFolder().then(setBooks);
  }, []);
  const [tags, setTags] = useState(book.tags);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ text: string; bad: boolean; queue?: boolean } | null>(null);

  useEffect(() => {
    setTags(book.tags);
    setMsg(null);
    setLocatePath("");
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

  const hasAudio = detail?.asin === book.asin && detail.audioFileExists;
  useEffect(() => {
    setFiles([]);
    if (!hasAudio) return;
    const ctl = new AbortController();
    api.audioFiles(book.asin, ctl.signal).then(setFiles, () => undefined);
    return () => ctl.abort();
  }, [book.asin, hasAudio, refresh]);

  useEffect(() => {
    if (variant === "panel") return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose, variant]);

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

  const upload = async () => {
    setBusy(true);
    setMsg(null);
    try {
      const result = (await api.absUpload([book.asin])).results[0]?.result;
      if (result === "ok") setMsg({ text: "Upload to Audiobookshelf queued.", bad: false, queue: true });
      else if (result === "conflict") setMsg({ text: "Not uploaded: the book is not liberated or already has a queued job.", bad: true });
      else setMsg({ text: "Not uploaded: the daemon does not know this book.", bad: true });
    } catch (e) {
      setMsg({ text: `Upload to Audiobookshelf: ${(e as Error).message}`, bad: true });
    } finally {
      setBusy(false);
    }
  };

  const convert = async () => {
    setBusy(true);
    setMsg(null);
    try {
      await api.convertBook(book.asin);
      setMsg(convertOutcome("ok"));
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) setMsg(convertOutcome("conflict"));
      else setMsg({ text: `Convert to MP3: ${(e as Error).message}`, bad: true });
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
    ["Narrators", book.narrators.join(", ") || "none"],
    ["Series", seriesLinks],
    ["Length", formatLength(book.lengthInMinutes)],
    ["Purchased", formatDate(book.dateAdded) || "unknown"],
    ["Publisher", d?.publisher || "unknown"],
    ["Language", d?.language || "unknown"],
    ["Published", formatDate(d?.datePublished) || "unknown"],
    ["Abridged", d ? (d.isAbridged ? "yes" : "no") : "..."],
    ["Account", book.account],
    ["Locale", book.locale],
    ["Content type", humanize(book.contentType)],
    ["Podcast episode", book.isPodcastEpisode ? "yes" : "no"],
    ["Audible Plus", book.isAudiblePlus ? (book.includedUntil ? `yes, until ${formatDate(book.includedUntil)}` : "yes") : "no"],
    ["Spatial audio", book.isSpatial ? "yes" : "no"],
    ["Rating", ratingText(book.productRating)],
    ["My rating", ratingText(book.myRating)],
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

  const state = lampState(book);
  const head = (
    <>
      <div className="insp-head">
        <Cover asin={book.asin} pictureId={book.pictureId} size="large" />
        <div className="insp-title">
          <h2>{book.title}</h2>
          {book.subtitle && <div className="muted">{book.subtitle}</div>}
          <div className="insp-author">{book.authors.join(", ")}</div>
          <div className="insp-pills">
            <span className={`status-pill s-${state}`}>{lampLabel(book)}</span>
            {book.isAudiblePlus && <span className="status-pill s-plus">Audible Plus</span>}
            {book.pdfStatus && <span className={`status-pill ${book.pdfStatus === "Liberated" ? "s-green" : "s-red"}`}>PDF {book.pdfStatus === "Liberated" ? "downloaded" : "not downloaded"}</span>}
          </div>
        </div>
      </div>
      <div className="insp-actions">
        {d?.audioFileExists && (
          <button className="btn primary" onClick={() => player.play(book)} disabled={player.playing?.asin === book.asin}>
            <Icon name="play" size={14} /> {player.playing?.asin === book.asin ? "Playing" : "Play"}
          </button>
        )}
        {!book.inTrash && book.bookStatus !== "Liberated" && (
          <button className="btn primary" onClick={() => void run("Liberate", () => api.liberateBook(book.asin), "Queued for liberation.")} disabled={busy}>
            Liberate
          </button>
        )}
      </div>
    </>
  );

  const Wrapper = variant === "panel" ? "div" : "aside";
  return (
    <>
      {variant !== "panel" && <div className="backdrop" onClick={onClose} />}
      <Wrapper className={`details details-${variant}`} role={variant === "panel" ? undefined : "dialog"} aria-label={`Details for ${book.title}`}>
        {variant !== "panel" && (
          <div className="sheet-bar">
            <button className="btn subtle back" onClick={onClose} aria-label="Close details">
              <Icon name={variant === "sheet" ? "chevronLeft" : "close"} size={16} /> {variant === "sheet" ? "Library" : "Close"}
            </button>
          </div>
        )}
        {head}
        {book.parentAsin && (
          <p>
            <button className="btn" onClick={() => onSearch(seriesQuery(book.parentAsin!))}>Show episodes of this podcast</button>
          </p>
        )}
        {detailError && <p className="error">Details unavailable: {detailError}</p>}
        {description && (
          <>
            <h3>Description</h3>
            <p className="description">{description}</p>
          </>
        )}

        <h3>Details</h3>
        <dl className="kv">
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

        <SeriesPanel book={book} onShow={(id) => onSearch(seriesQuery(id), seriesSort)} />

        {canRate(book) && <RatingEditor asin={book.asin} current={book.myRating} onSaved={(b) => { onChange(b); setRefresh((n) => n + 1); }} />}

        {!book.inTrash && <ProgressPanel asin={book.asin} lengthMinutes={book.lengthInMinutes} disabled={busy} />}

        <BookmarksExport asin={book.asin} />

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

        <h3>Download status</h3>
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

        <h3>Locate file</h3>
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault();
            if (locatePath.trim()) void run("Locate file", () => api.locateFile(book.asin, locatePath.trim()), "File located; marked liberated.");
          }}
        >
          <input value={locatePath} onChange={(e) => setLocatePath(e.target.value)} placeholder={books ? `${books.replace(/\/$/, "")}/Author/Book.m4b` : "/absolute/path/book.m4b"} aria-label="Path of an existing audio file on the server" disabled={busy} />
          <button type="submit" disabled={busy || !locatePath.trim()}>
            Locate
          </button>
        </form>
        <p className="muted small">An existing audio file on the server{books ? <>, inside <code>{books}</code></> : ""}.</p>

        <h3>More</h3>
        <div className="row drawer-actions">
          {!book.inTrash && book.bookStatus === "Liberated" && (
            <button onClick={() => void run("Liberate", () => api.liberateBook(book.asin), "Queued for liberation.")} disabled={busy}>
              Download again
            </button>
          )}
          {canConvertToMp3(book, files) && (
            <button onClick={() => void convert()} disabled={busy} title="Re-encode the m4b to mp3 with the saved MP3 settings">
              Convert to MP3
            </button>
          )}
          {!book.inTrash && (
            <button onClick={() => void upload()} disabled={busy}>
              Upload to Audiobookshelf
            </button>
          )}
          <button onClick={() => void run(book.inTrash ? "Restore" : "Trash", () => (book.inTrash ? api.restore(book.asin) : api.trash(book.asin)))} disabled={busy}>
            {book.inTrash ? "Restore" : "Move to trash"}
          </button>
        </div>
        {msg && (
          <p className={msg.bad ? "error" : "muted"}>
            {msg.text}
            {msg.queue && (
              <>
                {" "}
                <a href="#/queue">View queue</a>
              </>
            )}
          </p>
        )}
      </Wrapper>
    </>
  );
}
