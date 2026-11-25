import { useState } from "react";
import { api } from "../api/client";
import { formatTime, humanize } from "../format";
import { useLive } from "../live";

export function StatusBar() {
  const { connected, status, statusError, setStatus } = useLive();
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);
  const [busy, setBusy] = useState(false);

  const run = async (kind: "scan" | "liberate") => {
    setBusy(true);
    setMessage(null);
    try {
      setStatus(await (kind === "scan" ? api.scan() : api.liberate()));
      setMessage({ text: kind === "scan" ? "Scan requested." : "Scan and liberate requested.", error: false });
    } catch (e) {
      setMessage({ text: (e as Error).message, error: true });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="statusbar">
      <div className="status-info">
        <span className={`dot ${connected ? "ok" : "bad"}`} title={connected ? "Live updates connected" : "Live updates disconnected"} />
        {status ? (
          <>
            <span className={`pill state-${status.state}`}>{humanize(status.state)}</span>
            <span className="muted">last run {formatTime(status.lastRun)}</span>
            <span className="muted">next {formatTime(status.nextRun)}</span>
            {status.version && <span className="muted">v{status.version}</span>}
          </>
        ) : (
          <span className="muted">{statusError ?? "Connecting..."}</span>
        )}
      </div>
      <div className="status-actions">
        {message && <span className={message.error ? "error" : "muted"}>{message.text}</span>}
        <button onClick={() => run("scan")} disabled={busy}>
          Scan all
        </button>
        <button onClick={() => run("liberate")} disabled={busy}>
          Scan + liberate
        </button>
      </div>
    </div>
  );
}
