import { useState } from "react";
import { api } from "../api/client";
import type { Bookmark } from "../api/types";
import { bookmarkFormats, bookmarkLine, countLine } from "../bookmarks";

export function BookmarksExport({ asin }: { asin: string }) {
  const [state, setState] = useState<{ asin: string; list: Bookmark[] } | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const list = state?.asin === asin ? state.list : null;

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      setState({ asin, list: await api.bookmarks(asin) });
    } catch (e) {
      setError(`Bookmarks: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <h3>Clips and bookmarks</h3>
      <div className="row">
        <button onClick={() => void load()} disabled={loading}>
          {loading ? "Fetching from Audible..." : list ? "Refresh" : "Export bookmarks..."}
        </button>
        {list && list.length > 0 &&
          bookmarkFormats.map((f) => (
            <a key={f.id} className="button" href={api.bookmarksUrl(asin, f.id)} download>
              {f.label}
            </a>
          ))}
      </div>
      {error && <p className="error">{error}</p>}
      {list && <p className="muted small">{countLine(list)}</p>}
      {list && list.length > 0 && (
        <ul className="plain small bookmark-list">
          {list.slice(0, 20).map((b, i) => (
            <li key={i}>
              {bookmarkLine(b)}
              {(b.title || b.note) && <span className="muted"> {[b.title, b.note].filter(Boolean).join(": ")}</span>}
            </li>
          ))}
          {list.length > 20 && <li className="muted">and {list.length - 20} more in the file.</li>}
        </ul>
      )}
    </>
  );
}
