import { useState } from "react";
import { addFilter, label, moveFilter, removeFilter, updateFilter } from "../quickFilters";
import type { useQuickFilters } from "../useQuickFilters";
import { Popover } from "./Popover";

interface Props {
  current: string;
  qf: ReturnType<typeof useQuickFilters>;
  onApply: (q: string) => void;
}

export function QuickFilters({ current, qf, onApply }: Props) {
  const { draft, filters, rowError, saveError, loadError, legacy, commit, importLegacy, discardLegacy } = qf;
  const [editing, setEditing] = useState(false);
  const useDefault = draft?.useDefault ?? false;
  const set = (next: typeof filters) => draft && commit({ ...draft, filters: next });

  return (
    <Popover label="Quick filters" className="wide">
      {(close) => (
        <div>
          {loadError && <p className="error">{loadError}</p>}
          {legacy.length > 0 && (
            <div className="card attention">
              <p className="small">
                {legacy.length} quick {legacy.length === 1 ? "filter is" : "filters are"} saved in this browser only. Import {legacy.length === 1 ? "it" : "them"} to the server?
              </p>
              <div className="row">
                <button onClick={importLegacy}>Import</button>
                <button onClick={discardLegacy}>Discard</button>
              </div>
            </div>
          )}
          {draft && filters.length === 0 && <p className="muted small">No saved filters. Type a search, then add it here.</p>}
          <ul className="plain">
            {filters.map((f, i) => (
              <li key={`${i}:${f.filter}:${f.name ?? ""}`} className="qf-item">
                <div className="qf-row">
                  {editing ? (
                    <input
                      className="qf-name"
                      defaultValue={f.name ?? ""}
                      placeholder="Name (optional)"
                      aria-label={`Name for ${f.filter}`}
                      onBlur={(e) => e.target.value !== (f.name ?? "") && set(updateFilter(filters, i, { name: e.target.value.trim() || null }))}
                    />
                  ) : (
                    <button
                      className="qf-apply"
                      onClick={() => {
                        onApply(f.filter);
                        close();
                      }}
                      title={f.filter}
                    >
                      {label(f)}
                    </button>
                  )}
                  {editing && (
                    <>
                      <button onClick={() => set(moveFilter(filters, i, -1))} disabled={i === 0} aria-label={`Move ${label(f)} up`}>
                        &uarr;
                      </button>
                      <button onClick={() => set(moveFilter(filters, i, 1))} disabled={i === filters.length - 1} aria-label={`Move ${label(f)} down`}>
                        &darr;
                      </button>
                      <button onClick={() => set(removeFilter(filters, i))} aria-label={`Remove ${label(f)}`}>
                        Remove
                      </button>
                    </>
                  )}
                </div>
                {editing && (
                  <input
                    className="qf-filter"
                    defaultValue={f.filter}
                    aria-label={`Filter text ${i + 1}`}
                    aria-invalid={rowError?.index === i}
                    onBlur={(e) => e.target.value !== f.filter && set(updateFilter(filters, i, { filter: e.target.value }))}
                  />
                )}
                {rowError?.index === i && <div className="error small" role="alert">{rowError.message}</div>}
              </li>
            ))}
          </ul>
          {saveError && <p className="error small" role="alert">{saveError}</p>}
          {rowError && <p className="muted small">Not saved yet; fix or remove the filter above.</p>}
          <label className="check-label small">
            <input type="checkbox" checked={useDefault} disabled={!draft} onChange={(e) => draft && commit({ ...draft, useDefault: e.target.checked })} />
            Apply the first filter when the library loads
          </label>
          <div className="qf-foot">
            <button onClick={() => set(addFilter(filters, current))} disabled={!draft || !current.trim() || filters.some((f) => f.filter === current.trim())}>
              Add current search
            </button>
            <button onClick={() => setEditing((e) => !e)} disabled={filters.length === 0}>
              {editing ? "Done" : "Edit"}
            </button>
          </div>
        </div>
      )}
    </Popover>
  );
}
