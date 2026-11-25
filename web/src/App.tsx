import { useEffect, useState } from "react";
import { LibraryPage } from "./components/LibraryPage";
import { QueuePage } from "./components/QueuePage";
import { SettingsPage } from "./components/SettingsPage";
import { StatusBar } from "./components/StatusBar";
import { useLive } from "./live";

const tabs = [
  { id: "library", label: "Library" },
  { id: "queue", label: "Queue" },
  { id: "settings", label: "Settings" },
] as const;

type Tab = (typeof tabs)[number]["id"];

function currentTab(): Tab {
  const id = window.location.hash.replace(/^#\/?/, "");
  return tabs.find((t) => t.id === id)?.id ?? "library";
}

export function App() {
  const [tab, setTab] = useState<Tab>(currentTab);
  const { queue } = useLive();

  useEffect(() => {
    const onHash = () => setTab(currentTab());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const active = queue.jobs.length + queue.pending.length + (queue.waiting ? 1 : 0);

  return (
    <>
      <header>
        <h1>Libation</h1>
        <nav>
          {tabs.map((t) => (
            <a key={t.id} href={`#/${t.id}`} className={tab === t.id ? "active" : ""}>
              {t.label}
              {t.id === "queue" && active > 0 && <span className="badge">{active}</span>}
              {t.id === "queue" && queue.stats.errors > 0 && (
                <span className="badge bad" title={`${queue.stats.errors} failed`} aria-label={`${queue.stats.errors} failed`}>
                  !
                </span>
              )}
            </a>
          ))}
        </nav>
      </header>
      <StatusBar />
      <main>
        {tab === "library" && <LibraryPage />}
        {tab === "queue" && <QueuePage />}
        {tab === "settings" && <SettingsPage />}
      </main>
    </>
  );
}
