import type { Stats } from "../api/types";
import { humanize, shortTime } from "../format";
import { useLive } from "../live";
import { addFilter, label } from "../quickFilters";
import { routeHref, scopes, type Route, type Scope } from "../route";
import type { useQuickFilters } from "../useQuickFilters";
import { Icon } from "./Icon";
import { QuickFilters } from "./QuickFilters";

interface Props {
  route: Route;
  stats: Stats | null;
  qf: ReturnType<typeof useQuickFilters>;
  search: string;
  onApplyFilter: (q: string) => void;
  onNavigate?: () => void;
}

function scopeCount(id: Scope, s: Stats | null): number | null {
  if (!s) return null;
  switch (id) {
    case "all":
      return s.total;
    case "not-downloaded":
      return s.notLiberated;
    case "downloaded":
      return s.liberated;
    case "needs-attention":
      return s.error;
    case "unavailable":
      return s.unavailable;
    default:
      return null;
  }
}

export function Sidebar({ route, stats, qf, search, onApplyFilter, onNavigate }: Props) {
  const { queue, status, connected, statusError } = useLive();
  const active = queue.jobs.length + queue.pending.length + (queue.waiting ? 1 : 0);
  const needLogin = status?.accountsNeedingLogin?.length ?? 0;
  const onLibrary = route.page === "library";
  const typed = search.trim();
  const canSave = !!qf.draft && !!typed && !qf.filters.some((f) => f.filter === typed);

  const scopeLink = (id: Scope, text: string, icon?: Parameters<typeof Icon>[0]["name"]) => {
    const n = scopeCount(id, stats);
    const current = onLibrary && route.scope === id && (id !== "all" || !typed || !qf.filters.some((f) => f.filter === typed));
    return (
      <a key={id} href={routeHref({ scope: id })} className={`side-item ${current ? "current" : ""}`} aria-current={current ? "page" : undefined} onClick={onNavigate}>
        {icon && <Icon name={icon} size={16} />}
        <span className="side-label">{text}</span>
        {n != null && <span className="side-count">{n}</span>}
      </a>
    );
  };

  const pageLink = (page: Route["page"], text: string, icon: Parameters<typeof Icon>[0]["name"], badge?: React.ReactNode) => (
    <a href={routeHref({ page })} className={`side-item ${route.page === page ? "current" : ""}`} aria-current={route.page === page ? "page" : undefined} onClick={onNavigate}>
      <Icon name={icon} size={16} />
      <span className="side-label">{text}</span>
      {badge}
    </a>
  );

  return (
    <nav className="sidebar" aria-label="Library">
      <div className="side-scroll">
        <div className="side-heading">Library</div>
        {scopes.filter((s) => s.group === "library").map((s) => scopeLink(s.id, s.label))}

        <div className="side-heading">Browse</div>
        {scopes.filter((s) => s.group === "browse").map((s) => scopeLink(s.id, s.label))}

        <div className="side-heading side-heading-row">
          <span>Quick filters</span>
          <QuickFilters current={search} qf={qf} onApply={(q) => { onApplyFilter(q); onNavigate?.(); }} label="Edit" />
        </div>
        {qf.filters.map((f) => {
          const current = onLibrary && typed === f.filter;
          return (
            <button key={`${f.filter}:${f.name ?? ""}`} className={`side-item ${current ? "current" : ""}`} title={f.filter} onClick={() => { onApplyFilter(f.filter); onNavigate?.(); }}>
              <span className="side-label">{label(f)}</span>
            </button>
          );
        })}
        <button className="side-item side-add" disabled={!canSave} onClick={() => qf.draft && qf.commit({ ...qf.draft, filters: addFilter(qf.filters, typed) })} title={canSave ? `Save "${typed}"` : "Type a search to save it"}>
          <Icon name="plus" size={14} /> Save current search
        </button>
      </div>

      <div className="side-bottom">
        {scopeLink("trash", "Trash", "trash")}
        {pageLink("queue", "Queue", "queue", <>
          {active > 0 && <span className="side-badge">{active}</span>}
          {queue.stats.errors > 0 && <span className="side-badge bad" title={`${queue.stats.errors} failed`}>!</span>}
        </>)}
        {pageLink("accounts", "Accounts", "account", needLogin > 0 ? <span className="side-badge bad" title={`${needLogin} need sign-in`}>!</span> : undefined)}
        {pageLink("settings", "Settings", "gear")}
        <div className="side-status" title={connected ? "Live updates connected" : "Live updates disconnected"}>
          <span className={`dot ${connected ? "ok" : "bad"}`} />
          {status ? (
            <span>
              {humanize(status.state)}
              {status.nextRun && <> · next run {shortTime(status.nextRun)}</>}
            </span>
          ) : (
            <span>{statusError ?? "Connecting..."}</span>
          )}
        </div>
      </div>
    </nav>
  );
}
