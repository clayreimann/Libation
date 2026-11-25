import { useState } from "react";
import { addFilter, loadFilters, moveFilter, removeFilter, saveFilters } from "../quickFilters";
import { Popover } from "./Popover";

interface Props {
  current: string;
  onApply: (q: string) => void;
}

export function QuickFilters({ current, onApply }: Props) {
  const [filters, setFilters] = useState<string[]>(loadFilters);
  const [editing, setEditing] = useState(false);

  const update = (next: string[]) => {
    setFilters(next);
    saveFilters(next);
  };

  return (
    <Popover label="Quick filters" className="wide">
      {(close) => (
        <div>
          {filters.length === 0 && <p className="muted small">No saved filters. Type a search, then add it here.</p>}
          <ul className="plain">
            {filters.map((f, i) => (
              <li key={f} className="qf-row">
                <button
                  className="qf-apply"
                  onClick={() => {
                    onApply(f);
                    close();
                  }}
                  title={f}
                >
                  {f}
                </button>
                {editing && (
                  <>
                    <button onClick={() => update(moveFilter(filters, i, -1))} disabled={i === 0} aria-label={`Move ${f} up`}>
                      &uarr;
                    </button>
                    <button onClick={() => update(moveFilter(filters, i, 1))} disabled={i === filters.length - 1} aria-label={`Move ${f} down`}>
                      &darr;
                    </button>
                    <button onClick={() => update(removeFilter(filters, i))} aria-label={`Remove ${f}`}>
                      Remove
                    </button>
                  </>
                )}
              </li>
            ))}
          </ul>
          <div className="qf-foot">
            <button onClick={() => update(addFilter(filters, current))} disabled={!current.trim() || filters.includes(current.trim())}>
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
