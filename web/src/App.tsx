import { useEffect, useState } from "react";
import { AccountsPage } from "./components/AccountsPage";
import { LibraryPage } from "./components/LibraryPage";
import { QueuePage } from "./components/QueuePage";
import { NeedsLoginBanner } from "./components/NeedsLoginBanner";
import { SettingsPage } from "./components/SettingsPage";
import { StatusBar } from "./components/StatusBar";
import { useLive } from "./live";

const tabs = [
  { id: "library", label: "Library" },
  { id: "queue", label: "Queue" },
  { id: "accounts", label: "Accounts" },
  { id: "settings", label: "Settings" },
] as const;

type Tab = (typeof tabs)[number]["id"];

function currentTab(): Tab {
  const id = window.location.hash.replace(/^#\/?/, "");
  return tabs.find((t) => t.id === id)?.id ?? "library";
}

export function App() {
  const [tab, setTab] = useState<Tab>(currentTab);
  const { queue, status } = useLive();
  const [handoff, setHandoff] = useState<string | undefined>();

  useEffect(() => {
    const onHash = () => {
      const next = currentTab();
      if (next !== "library") setHandoff(undefined);
      setTab(next);
    };
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const needLogin = status?.accountsNeedingLogin?.length ?? 0;
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
              {t.id === "accounts" && needLogin > 0 && (
                <span className="badge bad" title={`${needLogin} need sign-in`} aria-label={`${needLogin} need sign-in`}>
                  !
                </span>
              )}
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
      <NeedsLoginBanner />
      <main>
        {tab === "library" && <LibraryPage initialQuery={handoff} />}
        {tab === "queue" && (
          <QueuePage
            onSearch={(q) => {
              setHandoff(q);
              window.location.hash = "#/library";
            }}
          />
        )}
        {tab === "accounts" && <AccountsPage />}
        {tab === "settings" && <SettingsPage />}
      </main>
    </>
  );
}
