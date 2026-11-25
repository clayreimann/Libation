import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Settings } from "../api/types";
import { humanize } from "../format";

function Section({ title, data }: { title: string; data: object }) {
  return (
    <section>
      <h3>{title}</h3>
      <dl>
        {Object.entries(data).map(([k, v]) => (
          <div key={k}>
            <dt>{humanize(k)}</dt>
            <dd>{typeof v === "boolean" ? (v ? "Yes" : "No") : String(v ?? "-")}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

export function SettingsPage() {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.settings().then(setSettings, (e: Error) => setError(e.message));
  }, []);

  if (error) return <p className="error">{error}</p>;
  if (!settings) return <p className="muted">Loading...</p>;

  const { books, inProgress, naming, download, queue, library, audiobookshelf } = settings;
  return (
    <div className="settings">
      <p className="muted">Read-only. Change settings in Settings.json on the daemon.</p>
      <p>
        Accounts are managed on the <a href="#/accounts">Accounts</a> page.
      </p>
      <Section title="Folders" data={{ books, inProgress }} />
      <Section title="Naming" data={naming} />
      <Section title="Download" data={download} />
      <Section title="Queue" data={queue} />
      <Section title="Library" data={library} />
      <Section title="Audiobookshelf" data={audiobookshelf} />
    </div>
  );
}
