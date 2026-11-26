import { useEffect, useState } from "react";
import { api } from "../api/client";
import { confirmPhrase, phraseMatches, plusTargets, reduceRemoval, removalLine, REMOVE_MAX, type RemovalSummary } from "../removeAudible";

interface Props {
  selected: string[];
  titles: ReadonlyMap<string, string>;
  onClose: () => void;
  onRemoved: (asins: string[]) => void;
}

export function RemoveFromAudible({ selected, titles, onClose, onRemoved }: Props) {
  const [plus, setPlus] = useState<Set<string> | null>(null);
  const [incomplete, setIncomplete] = useState(false);
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<RemovalSummary | null>(null);

  useEffect(() => {
    const ctl = new AbortController();
    api.asins({ q: "AudiblePlus", trash: false }, ctl.signal).then(
      (r) => {
        setPlus(new Set(r.asins));
        setIncomplete(r.asins.length < r.total);
      },
      (e: Error) => !ctl.signal.aborted && setError(`Could not find which selected books are Audible Plus: ${e.message}`),
    );
    return () => ctl.abort();
  }, []);

  const t = plus ? plusTargets(selected, plus) : null;
  const n = t?.targets.length ?? 0;
  const phrase = confirmPhrase(n);

  const remove = async () => {
    if (!t) return;
    setBusy(true);
    setError(null);
    try {
      const s = reduceRemoval(await api.removeFromAudible(t.targets, typed));
      setSummary(s);
      if (s.removed.length) onRemoved(s.removed);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const name = (asin: string) => titles.get(asin) ?? asin;

  return (
    <div className="card attention" role="alertdialog" aria-label="Remove from Audible">
      <div className="job-head">
        <strong>Remove from Audible</strong>
        <button onClick={onClose} disabled={busy}>
          {summary ? "Dismiss" : "Cancel"}
        </button>
      </div>
      {!plus && !error && <p className="muted">Checking which selected books are Audible Plus...</p>}
      {t && !summary && (
        <>
          <p>
            This removes <strong>{n}</strong> Audible Plus {n === 1 ? "book" : "books"} from your Audible account and then deletes {n === 1 ? "it" : "them"} from the Libation library for good (not the trash).{" "}
            <strong>This cannot be undone</strong>: the only way to get a book back is to add it again in Audible.
          </p>
          {t.excluded > 0 && (
            <p className="muted">
              {t.excluded} selected {t.excluded === 1 ? "book is" : "books are"} not Audible Plus and {t.excluded === 1 ? "is" : "are"} excluded.
            </p>
          )}
          {t.overLimit > 0 && (
            <p className="muted">
              At most {REMOVE_MAX} books per request: {t.overLimit} more Audible Plus {t.overLimit === 1 ? "book was" : "books were"} left out. Run it again for the rest.
            </p>
          )}
          {incomplete && <p className="muted">The Plus list was truncated by the API limit, so some selected Plus books may have been treated as excluded.</p>}
          {n === 0 ? (
            <p className="error">None of the selected books are Audible Plus.</p>
          ) : (
            <form
              className="row"
              onSubmit={(e) => {
                e.preventDefault();
                if (phraseMatches(typed, n)) void remove();
              }}
            >
              <label htmlFor="remove-confirm">
                Type <code>{phrase}</code> to confirm
              </label>
              <input id="remove-confirm" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} disabled={busy} />
              <button type="submit" className="primary" disabled={busy || !phraseMatches(typed, n)}>
                {busy ? "Removing..." : `Remove ${n} from Audible`}
              </button>
            </form>
          )}
        </>
      )}
      {error && <p className="error">{error}</p>}
      {summary && (
        <>
          <p className={summary.failed.length ? "error" : "muted"}>{removalLine(summary)}</p>
          <ul className="plain small">
            {summary.removed.map((a) => (
              <li key={a}>{name(a)}: removed</li>
            ))}
            {summary.failed.map((f) => (
              <li key={f.asin} className="error">
                {name(f.asin)}: failed ({f.error})
              </li>
            ))}
            {summary.notPlus.map((a) => (
              <li key={a}>{name(a)}: not Audible Plus, not sent</li>
            ))}
            {summary.notFound.map((a) => (
              <li key={a}>{name(a)}: not found</li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
