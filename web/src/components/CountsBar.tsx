import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Stats } from "../api/types";
import { countQueries } from "../filters";

interface Props {
  refreshKey: number;
  onApply: (q: string) => void;
}

export function CountsBar({ refreshKey, onApply }: Props) {
  const [stats, setStats] = useState<Stats | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const ctl = new AbortController();
    api.stats(ctl.signal).then(
      (s) => {
        setStats(s);
        setFailed(false);
      },
      () => !ctl.signal.aborted && setFailed(true),
    );
    return () => ctl.abort();
  }, [refreshKey]);

  if (!stats) return failed ? <p className="muted small">Counts unavailable.</p> : null;

  const pdfTotal = stats.pdfLiberated + stats.pdfNotLiberated + stats.pdfUnavailable;
  // partial has no search field (it is a file on disk), so it is not clickable
  const items: { key: string; label: string; value: string | number; q?: string; cls?: string; show: boolean }[] = [
    { key: "notLiberated", label: "Not liberated", value: stats.notLiberated, q: countQueries.notLiberated, show: true },
    { key: "partial", label: "Partial", value: stats.partial, cls: "warn", show: stats.partial > 0 },
    { key: "liberated", label: "Fully liberated", value: stats.liberated, q: countQueries.liberated, cls: "ok", show: true },
    { key: "error", label: "Errors", value: stats.error, q: countQueries.error, cls: "bad", show: stats.error > 0 },
    { key: "unavailable", label: "Unavailable", value: stats.unavailable, q: countQueries.unavailable, cls: "warn", show: stats.unavailable > 0 },
    { key: "pdf", label: "PDFs downloaded", value: `${stats.pdfLiberated} of ${pdfTotal}`, q: countQueries.pdf, show: pdfTotal > 0 },
  ];

  return (
    <div className="counts" aria-label="Library counts">
      {items
        .filter((i) => i.show)
        .map((i) =>
          i.q ? (
            <button key={i.key} className={`count ${i.cls ?? ""}`} onClick={() => onApply(i.q!)} title={i.q}>
              <strong>{i.value}</strong> {i.label}
            </button>
          ) : (
            <span key={i.key} className={`count static ${i.cls ?? ""}`} title="Has a partly downloaded file; not searchable">
              <strong>{i.value}</strong> {i.label}
            </span>
          ),
        )}
    </div>
  );
}
