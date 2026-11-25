import { useEffect, useState } from "react";
import { api } from "../api/client";
import { booksFolder } from "../booksFolder";
import { formatTime } from "../format";
import { useLive } from "../live";

export function LocatePanel() {
  const { locateScan: scan } = useLive();
  const [folder, setFolder] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);

  useEffect(() => {
    void booksFolder().then((b) => setFolder((f) => f || b));
  }, []);

  const running = scan?.state === "running";

  const start = async () => {
    setStarting(true);
    setError(null);
    try {
      await api.startLocateScan(folder.trim());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setStarting(false);
    }
  };

  return (
    <div className="locate">
      <h3>Locate audiobooks</h3>
      <p className="muted small">Scans a server folder under the Books folder for .m4b and .mp3 files carrying an ASIN and marks those books liberated.</p>
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault();
          if (!running && folder.trim()) void start();
        }}
      >
        <input value={folder} onChange={(e) => setFolder(e.target.value)} aria-label="Folder to scan" placeholder="/data/Imported" disabled={running} />
        <button type="submit" className="primary" disabled={running || starting || !folder.trim()}>
          {running ? "Scanning..." : "Start scan"}
        </button>
      </form>
      {error && <p className="error">{error}</p>}
      {scan && scan.state !== "idle" && (
        <p className={scan.state === "failed" ? "error" : "muted"} aria-live="polite">
          {scan.state === "running" ? "Scanning" : scan.state === "completed" ? "Finished" : "Failed"} {scan.folder}: {scan.found} found, {scan.matched} in library, {scan.updated} newly liberated.
          {scan.error && ` ${scan.error}`}
          {scan.finishedAt && ` Finished ${formatTime(scan.finishedAt)}.`}
        </p>
      )}
    </div>
  );
}
