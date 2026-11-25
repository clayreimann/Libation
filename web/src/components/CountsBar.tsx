import { useEffect, useState } from "react";
import { api } from "../api/client";
import { countQueries } from "../filters";

interface Counts {
  liberated: number;
  notLiberated: number;
  error: number;
  unavailable: number;
  pdf: number;
  pdfDownloaded: number;
}

const total = (q: string, signal: AbortSignal) => api.library({ offset: 0, limit: 1, q, trash: false }, signal).then((p) => p.total);

async function load(signal: AbortSignal): Promise<Counts> {
  const [liberated, notLiberated, error, unavailable, pdfPage] = await Promise.all([
    total(countQueries.liberated, signal),
    total(countQueries.notLiberated, signal),
    total(countQueries.error, signal),
    total(countQueries.unavailable, signal),
    api.library({ offset: 0, limit: 1000, q: countQueries.pdf, trash: false }, signal),
  ]);
  const pdfDownloaded = pdfPage.items.filter((b) => b.pdfStatus === "Liberated").length;
  return { liberated, notLiberated, error, unavailable, pdf: pdfPage.total, pdfDownloaded };
}

interface Props {
  refreshKey: number;
  onApply: (q: string) => void;
}

export function CountsBar({ refreshKey, onApply }: Props) {
  const [counts, setCounts] = useState<Counts | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const ctl = new AbortController();
    load(ctl.signal).then(
      (c) => {
        setCounts(c);
        setFailed(false);
      },
      () => !ctl.signal.aborted && setFailed(true),
    );
    return () => ctl.abort();
  }, [refreshKey]);

  if (!counts) return failed ? <p className="muted small">Counts unavailable.</p> : null;

  const items: { key: string; label: string; n: number; q: string; cls?: string; show?: boolean }[] = [
    { key: "notLiberated", label: "Not liberated", n: counts.notLiberated, q: countQueries.notLiberated },
    { key: "liberated", label: "Fully liberated", n: counts.liberated, q: countQueries.liberated, cls: "ok" },
    { key: "error", label: "Errors", n: counts.error, q: countQueries.error, cls: "bad", show: counts.error > 0 },
    { key: "unavailable", label: "Unavailable", n: counts.unavailable, q: countQueries.unavailable, cls: "warn", show: counts.unavailable > 0 },
    {
      key: "pdf",
      label: "PDFs downloaded",
      n: counts.pdf,
      q: countQueries.pdf,
      show: counts.pdf > 0,
    },
  ];

  return (
    <div className="counts" aria-label="Library counts">
      {items
        .filter((i) => i.show !== false)
        .map((i) => (
          <button key={i.key} className={`count ${i.cls ?? ""}`} onClick={() => onApply(i.q)} title={i.q}>
            <strong>{i.key === "pdf" ? `${counts.pdfDownloaded} of ${counts.pdf}` : i.n}</strong> {i.label}
          </button>
        ))}
    </div>
  );
}
