import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { QualityApply, QualityResult, QualityScan } from "../api/types";
import { applyLine, bitrateGain, isScanning, qualityLabel, scanLine, toggleAll } from "../quality";
import { useLive } from "../live";

export function QualityPanel() {
  const { qualityScan: live } = useLive();
  const [fetched, setFetched] = useState<QualityScan | null>(null);
  const [widevine, setWidevine] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [applied, setApplied] = useState<{ result: QualityApply; asins: string[] } | null>(null);
  const [queued, setQueued] = useState<string | null>(null);

  const needsResults = (live?.state === "completed" || live?.state === "cancelled") && !live.results;
  const scan = needsResults && fetched?.state === live?.state ? fetched : live;
  const scanning = isScanning(scan);

  useEffect(() => {
    if (!needsResults) return;
    api.qualityScan().then(setFetched, (e: Error) => setError(e.message));
  }, [needsResults]);

  const results: QualityResult[] = scan?.results ?? [];

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
      setApplied(null);
      setQueued(null);
      setSelected(new Set());
      setFetched(null);
      await api.startQualityScan({ widevine: widevine || undefined });
    });

  const apply = () =>
    act("Apply", async () => {
      const asins = [...selected];
      setApplied({ result: await api.applyQuality(asins), asins });
      setQueued(null);
      setSelected(new Set());
    });

  const download = () =>
    act("Queue downloads", async () => {
      const s = await api.bulkLiberate(applied?.asins ?? []);
      setQueued(`${s.updated} queued.`);
    });

  return (
    <div className="quality">
      <h3>Find better quality</h3>
      <p className="muted small">Compares each downloaded book with the best format Audible offers now. Podcasts, spatial books and books not downloaded are skipped.</p>
      <div className="row">
        <label className="check-label">
          <input type="checkbox" checked={widevine} onChange={(e) => setWidevine(e.target.checked)} disabled={scanning} />
          Ask for Widevine formats
        </label>
        <button className="primary" onClick={() => void start()} disabled={busy || scanning}>
          {scanning ? "Scanning..." : "Start scan"}
        </button>
        {scanning && (
          <button onClick={() => void act("Cancel", async () => void (await api.cancelQualityScan()))} disabled={busy || scan?.state === "cancelling"}>
            Cancel
          </button>
        )}
      </div>
      {error && <p className="error">{error}</p>}
      {scan && scan.state !== "idle" && (
        <p className={scan.state === "failed" ? "error" : "muted"} aria-live="polite">
          {scanLine(scan)}
        </p>
      )}
      {results.length > 0 && !scanning && (
        <>
          <div className="row">
            <button onClick={() => setSelected(toggleAll(selected, results))}>{results.every((r) => selected.has(r.asin)) ? "Clear" : "Select all"}</button>
            <button className="primary" onClick={() => void apply()} disabled={busy || selected.size === 0}>
              Apply to {selected.size} selected
            </button>
          </div>
          <ul className="plain quality-results">
            {results.map((r) => {
              const gain = bitrateGain(r);
              return (
                <li key={r.asin}>
                  <label className="check-label">
                    <input type="checkbox" checked={selected.has(r.asin)} onChange={() => setSelected((s) => { const n = new Set(s); if (!n.delete(r.asin)) n.add(r.asin); return n; })} disabled={busy} />
                    <span>
                      {r.title}
                      <span className="muted small">
                        {" "}
                        {qualityLabel(r.currentQuality)} {"→"} {qualityLabel(r.availableQuality)}
                        {gain != null && ` (+${gain}%)`}
                      </span>
                    </span>
                  </label>
                </li>
              );
            })}
          </ul>
        </>
      )}
      {applied && (
        <>
          <p className="muted">{applyLine(applied.result)}</p>
          {applied.result.marked > 0 && (
            <div className="row">
              <button onClick={() => void download()} disabled={busy || queued != null}>
                Download now
              </button>
              <span className="muted small">{queued ?? "or wait for the next cycle."}</span>
              {queued && <a href="#/queue">View queue</a>}
            </div>
          )}
        </>
      )}
    </div>
  );
}
