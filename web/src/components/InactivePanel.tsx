import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { InactiveScan } from "../api/types";
import { inactiveLine, isInactiveScanning, remainingResults, toggleAllInactive } from "../inactive";
import { useLive } from "../live";

export function InactivePanel({ onTrashed }: { onTrashed: () => void }) {
  const { inactiveScan: live } = useLive();
  const [fetched, setFetched] = useState<InactiveScan | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [trashed, setTrashed] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const needsResults = (live?.state === "completed" || live?.state === "cancelled") && !live.results;
  const scan = needsResults && fetched?.state === live?.state ? fetched : live;
  const scanning = isInactiveScanning(scan);
  const results = remainingResults(scan, trashed);

  useEffect(() => {
    if (!needsResults) return;
    api.inactiveScan().then(setFetched, (e: Error) => setError(e.message));
  }, [needsResults]);

  const act = async (label: string, fn: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(`${label}: ${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const start = () =>
    act("Start scan", async () => {
      setSelected(new Set());
      setTrashed(new Set());
      setNote(null);
      setFetched(null);
      await api.startInactiveScan();
    });

  const trash = () =>
    act("Move to trash", async () => {
      const asins = [...selected];
      const s = await api.bulkTrash(asins);
      const failed = new Set([...s.conflict, ...s.notFound, ...s.failed.flatMap((f) => f.asins)]);
      const done = asins.filter((a) => !failed.has(a));
      setTrashed((t) => new Set([...t, ...done]));
      setSelected(new Set(failed));
      setNote(`${done.length} moved to the trash${failed.size ? `, ${failed.size} could not be moved` : ""}.`);
      onTrashed();
    });

  return (
    <div className="inactive">
      <h3>Scan for removed books</h3>
      <p className="muted small">Asks Audible which books that are not downloaded have left your account. Nothing is changed by the scan; you choose what to move to the trash.</p>
      <div className="row">
        <button className="primary" onClick={() => void start()} disabled={busy || scanning}>
          {scanning ? "Scanning..." : "Start scan"}
        </button>
        {scanning && (
          <button onClick={() => void act("Cancel", async () => void (await api.cancelInactiveScan()))} disabled={busy || scan?.state === "cancelling"}>
            Cancel
          </button>
        )}
      </div>
      {error && <p className="error">{error}</p>}
      {scan && scan.state !== "idle" && (
        <p className={scan.state === "failed" ? "error" : "muted"} aria-live="polite">
          {inactiveLine(scan, results.length)}
        </p>
      )}
      {results.length > 0 && !scanning && (
        <>
          <div className="row">
            <button onClick={() => setSelected(toggleAllInactive(selected, results))}>{results.every((r) => selected.has(r.asin)) ? "Clear" : "Select all"}</button>
            <button className="primary" onClick={() => void trash()} disabled={busy || selected.size === 0}>
              Move {selected.size} to trash
            </button>
          </div>
          <ul className="plain quality-results">
            {results.map((r) => (
              <li key={r.asin}>
                <label className="check-label">
                  <input
                    type="checkbox"
                    checked={selected.has(r.asin)}
                    disabled={busy}
                    onChange={() =>
                      setSelected((s) => {
                        const n = new Set(s);
                        if (!n.delete(r.asin)) n.add(r.asin);
                        return n;
                      })
                    }
                  />
                  <span>
                    {r.title}
                    <span className="muted small">
                      {" "}
                      {r.authors.join(", ")}
                      {r.authors.length ? ", " : ""}
                      {r.account}
                    </span>
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </>
      )}
      {note && <p className="muted">{note}</p>}
    </div>
  );
}
