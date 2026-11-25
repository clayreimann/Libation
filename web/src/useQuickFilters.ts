import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "./api/client";
import type { QuickFilter, QuickFilters } from "./api/types";
import { clearLegacyFilters, loadLegacyFilters, migrationOffer, parseFilterError, toNamed } from "./quickFilters";

export interface RowError {
  index: number;
  message: string;
}

export function useQuickFilters() {
  const [saved, setSaved] = useState<QuickFilters | null>(null);
  const [draft, setDraft] = useState<QuickFilters | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [rowError, setRowError] = useState<RowError | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [legacy, setLegacy] = useState<string[]>([]);
  const importing = useRef(false);

  useEffect(() => {
    api.quickFilters().then(
      (qf) => {
        setSaved(qf);
        setDraft(qf);
        setLegacy(migrationOffer(qf.filters, loadLegacyFilters()));
      },
      (e: Error) => setLoadError(e.message),
    );
  }, []);

  /** Shows `next` immediately and PUTs it; a 400 leaves it on screen with the error beside its row. */
  const commit = useCallback(async (next: QuickFilters): Promise<boolean> => {
    setDraft(next);
    setRowError(null);
    setSaveError(null);
    try {
      const res = await api.putQuickFilters(next);
      setSaved(res);
      setDraft(res);
      if (importing.current) {
        importing.current = false;
        clearLegacyFilters();
      }
      return true;
    } catch (e) {
      const parsed = e instanceof ApiError && e.status === 400 ? parseFilterError(e.message) : null;
      if (parsed) setRowError(parsed);
      else setSaveError((e as Error).message);
      return false;
    }
  }, []);

  const importLegacy = useCallback(() => {
    importing.current = true;
    setLegacy([]);
    void commit({ useDefault: draft?.useDefault ?? false, filters: toNamed(legacy) });
  }, [commit, draft, legacy]);

  const discardLegacy = useCallback(() => {
    clearLegacyFilters();
    setLegacy([]);
  }, []);

  const dirty = draft !== saved;
  const filters: QuickFilter[] = draft?.filters ?? [];
  return { saved, draft, filters, loadError, rowError, saveError, legacy, dirty, commit, importLegacy, discardLegacy };
}
