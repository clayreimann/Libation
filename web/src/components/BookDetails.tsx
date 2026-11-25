import { useEffect, useState } from "react";
import { api, ApiError, type LiberatedChoice } from "../api/client";
import type { Book } from "../api/types";
import { audibleUrl } from "../filters";
import { formatLength, formatTime, humanize } from "../format";
import { normalizeTags } from "../tags";

interface Props {
  book: Book;
  onChange: (b: Book) => void;
  onClose: () => void;
}

const statusValue = (s: string) => (s === "Liberated" ? "liberated" : s === "NotLiberated" ? "notLiberated" : "error");

export function BookDetails({ book, onChange, onClose }: Props) {
  const [tags, setTags] = useState(book.tags);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ text: string; bad: boolean } | null>(null);

  useEffect(() => {
    setTags(book.tags);
    setMsg(null);
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
  const series = book.series.map((s) => (s.order ? `${s.name} #${s.order}` : s.name)).join(", ");

  const rows: [string, React.ReactNode][] = [
    ["Authors", book.authors.join(", ") || "none"],
    ["Narrators", book.narrators.join(", ") || "none"],
    ["Series", series || "none"],
    ["Length", formatLength(book.lengthInMinutes)],
    ["Date added", formatTime(book.dateAdded)],
    ["Account", book.account],
    ["Locale", book.locale],
    ["Content type", humanize(book.contentType)],
    ["Podcast episode", book.isPodcastEpisode ? "yes" : "no"],
    ["Book status", <span className={`pill book-${book.bookStatus}`}>{book.bookStatus}</span>],
    ["PDF status", book.pdfStatus ? <span className={`pill book-${book.pdfStatus}`}>{book.pdfStatus}</span> : "no PDF"],
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

        <dl className="drawer-dl">
          {rows.map(([k, v]) => (
            <div key={k}>
              <dt>{k}</dt>
              <dd>{v}</dd>
            </div>
          ))}
        </dl>

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
