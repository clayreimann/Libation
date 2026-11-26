import { useCallback, useEffect, useState } from "react";
import { api } from "./api/client";
import type { Stats } from "./api/types";
import { AccountsPage } from "./components/AccountsPage";
import { Icon } from "./components/Icon";
import { LibraryPage } from "./components/LibraryPage";
import { NeedsLoginBanner } from "./components/NeedsLoginBanner";
import { QueuePage } from "./components/QueuePage";
import { SettingsPage } from "./components/SettingsPage";
import { Sidebar } from "./components/Sidebar";
import { useLive } from "./live";
import { readPref, writePref } from "./prefs";
import { parseRoute, routeHref, type Route } from "./route";
import { useQuickFilters } from "./useQuickFilters";

const SIDEBAR_KEY = "libation.sidebar";

export function App() {
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location.hash));
  const { queue, status, onBookEvent } = useLive();
  const qf = useQuickFilters();
  const [search, setSearch] = useState("");
  const [stats, setStats] = useState<Stats | null>(null);
  const [statsKey, setStatsKey] = useState(0);
  const [sidebarOpen, setSidebarOpen] = useState(() => readPref(SIDEBAR_KEY) !== false);
  const [drawer, setDrawer] = useState(false);

  useEffect(() => {
    const onHash = () => {
      setRoute(parseRoute(window.location.hash));
      setDrawer(false);
    };
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const refreshStats = useCallback(() => setStatsKey((n) => n + 1), []);
  useEffect(() => onBookEvent(refreshStats), [onBookEvent, refreshStats]);
  useEffect(() => {
    const ctl = new AbortController();
    api.stats(ctl.signal).then(setStats, () => undefined);
    return () => ctl.abort();
  }, [statsKey]);

  const toggleSidebar = () => {
    if (window.matchMedia("(max-width: 900px)").matches) return setDrawer((d) => !d);
    setSidebarOpen((o) => {
      writePref(SIDEBAR_KEY, !o);
      return !o;
    });
  };

  /** Quick filters and queue hand-offs search all books. */
  const searchAll = (q: string) => {
    setSearch(q);
    if (route.page !== "library" || route.scope !== "all") window.location.hash = routeHref({ scope: "all" });
  };

  const needLogin = status?.accountsNeedingLogin?.length ?? 0;
  const active = queue.jobs.length + queue.pending.length + (queue.waiting ? 1 : 0);

  return (
    <div className={`app ${sidebarOpen ? "" : "sidebar-closed"} ${drawer ? "drawer-open" : ""} page-${route.page}`}>
      {drawer && <div className="backdrop side-backdrop" onClick={() => setDrawer(false)} />}
      <Sidebar route={route} stats={stats} qf={qf} search={search} onApplyFilter={searchAll} onNavigate={() => setDrawer(false)} />
      <div className="main-col">
        <NeedsLoginBanner />
        {route.page === "library" && (
          <LibraryPage
            scope={route.scope}
            input={search}
            setInput={setSearch}
            qf={qf}
            stats={stats}
            onChanged={refreshStats}
            onToggleSidebar={toggleSidebar}
          />
        )}
        {route.page !== "library" && (
          <div className="page">
            <header className="page-head">
              <button className="icon-btn show-narrow" onClick={() => setDrawer(true)} aria-label="Show sidebar">
                <Icon name="menu" />
              </button>
              <h1>{route.page === "queue" ? "Queue" : route.page === "accounts" ? "Accounts" : "Settings"}</h1>
            </header>
            <div className="page-body">
              {route.page === "queue" && <QueuePage onSearch={searchAll} />}
              {route.page === "accounts" && <AccountsPage />}
              {route.page === "settings" && <SettingsPage />}
            </div>
          </div>
        )}
      </div>
      <nav className="tabbar" aria-label="Sections">
        <a href={routeHref({ page: "library" })} className={route.page === "library" ? "current" : ""}>
          <Icon name="library" size={22} />
          <span>Library</span>
        </a>
        <a href={routeHref({ page: "queue" })} className={route.page === "queue" ? "current" : ""}>
          <span className="tab-icon">
            <Icon name="queue" size={22} />
            {active > 0 && <span className="tab-badge">{active}</span>}
            {active === 0 && queue.stats.errors > 0 && <span className="tab-badge bad">!</span>}
          </span>
          <span>Queue</span>
        </a>
        <a href={routeHref({ page: "accounts" })} className={route.page === "accounts" ? "current" : ""}>
          <span className="tab-icon">
            <Icon name="account" size={22} />
            {needLogin > 0 && <span className="tab-badge bad">!</span>}
          </span>
          <span>Accounts</span>
        </a>
        <a href={routeHref({ page: "settings" })} className={route.page === "settings" ? "current" : ""}>
          <Icon name="gear" size={22} />
          <span>Settings</span>
        </a>
      </nav>
    </div>
  );
}
