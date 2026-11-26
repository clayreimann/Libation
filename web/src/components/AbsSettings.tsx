import { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "../api/client";
import type { AbsLibrary, Settings } from "../api/types";
import { defaultFolder, describeLibrariesError, describeTest, foldersOf, testRequest, type Outcome } from "../abs";
import { ABS_FIELDS, ABS_TOKEN, type Draft } from "../settingsForm";
import { FieldInput } from "./FieldInput";

interface Props {
  settings: Settings;
  draft: Draft;
  errors: Record<string, string>;
  clearToken: boolean;
  set: (key: string, v: string | boolean) => void;
  setClearToken: (v: boolean) => void;
}

const URL_KEY = "audiobookshelf.serverUrl";
const LIB_KEY = "audiobookshelf.libraryId";
const FOLDER_KEY = "audiobookshelf.folderId";

export function AbsSettings({ settings, draft, errors, clearToken, set, setClearToken }: Props) {
  const abs = settings.audiobookshelf;
  const canList = abs.serverConfigured && abs.tokenSet;
  const [libs, setLibs] = useState<AbsLibrary[] | null>(null);
  const [libsError, setLibsError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [testing, setTesting] = useState(false);
  const [test, setTest] = useState<Outcome | null>(null);

  const loadLibs = useCallback(async () => {
    setLoading(true);
    setLibsError(null);
    try {
      setLibs(await api.absLibraries());
    } catch (e) {
      setLibs(null);
      setLibsError(e instanceof ApiError ? describeLibrariesError(e.status, e.message, e.detail.code) : (e as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (canList) void loadLibs();
    else setLibs(null);
  }, [canList, abs.serverUrl, loadLibs]);

  const runTest = async () => {
    setTesting(true);
    setTest(null);
    try {
      setTest(describeTest(await api.absTest(testRequest(abs.serverUrl ?? "", String(draft[URL_KEY]), String(draft[ABS_TOKEN])))));
    } catch (e) {
      setTest({ ok: false, text: (e as Error).message });
    } finally {
      setTesting(false);
    }
  };

  const libraryId = String(draft[LIB_KEY]);
  const folders = libs ? foldersOf(libs, libraryId) : [];
  const unsavedConnection = String(draft[URL_KEY]).trim() !== (abs.serverUrl ?? "") || String(draft[ABS_TOKEN]) !== "" || clearToken;

  return (
    <section>
      <h3>Audiobookshelf</h3>
      <div className="fields">
        {ABS_FIELDS.map((f) => (
          <FieldInput key={f.key} field={f} draft={draft} errors={errors} set={set} />
        ))}
        <div className="field wide">
          <label htmlFor="s-abs-url">Server URL</label>
          <input id="s-abs-url" type="text" placeholder="https://abs.example.com" value={String(draft[URL_KEY])} aria-invalid={!!errors[URL_KEY]} onChange={(e) => set(URL_KEY, e.target.value)} />
          {errors[URL_KEY] && (
            <div className="error small" role="alert">
              {errors[URL_KEY]}
            </div>
          )}
        </div>
        <div className="field wide">
          <label htmlFor="s-abs-token">API token</label>
          <div className="row">
            <input
              id="s-abs-token"
              type="password"
              autoComplete="off"
              spellCheck={false}
              value={String(draft[ABS_TOKEN])}
              disabled={clearToken}
              placeholder={abs.tokenSet ? "Saved; type to replace" : "Not set"}
              aria-invalid={!!errors[ABS_TOKEN]}
              onChange={(e) => set(ABS_TOKEN, e.target.value)}
            />
            {abs.tokenSet && (
              <label className="check-label">
                <input type="checkbox" checked={clearToken} onChange={(e) => setClearToken(e.target.checked)} /> Remove saved token
              </label>
            )}
          </div>
          <div className="muted small">Write-only: the daemon never sends the token back.</div>
          {errors[ABS_TOKEN] && (
            <div className="error small" role="alert">
              {errors[ABS_TOKEN]}
            </div>
          )}
        </div>
        <div className="field wide">
          <div className="row">
            <button type="button" disabled={testing} onClick={runTest}>
              {testing ? "Testing..." : "Test connection"}
            </button>
            {test && (
              <span className={test.ok ? "" : "error"} role="status">
                {test.text}
              </span>
            )}
          </div>
          <div className="muted small">Uses the values above, saved or not; unsaved values are not stored.</div>
        </div>
        <div className="field">
          <label htmlFor="s-abs-library">Library</label>
          <select
            id="s-abs-library"
            value={libraryId}
            disabled={!libs}
            onChange={(e) => {
              set(LIB_KEY, e.target.value);
              set(FOLDER_KEY, libs ? defaultFolder(libs, e.target.value) : "");
            }}
          >
            <option value="">None</option>
            {libraryId && !libs?.some((l) => l.id === libraryId) && <option value={libraryId}>{libraryId}</option>}
            {libs?.map((l) => (
              <option key={l.id} value={l.id}>
                {l.name}
              </option>
            ))}
          </select>
          {errors[LIB_KEY] && (
            <div className="error small" role="alert">
              {errors[LIB_KEY]}
            </div>
          )}
        </div>
        <div className="field">
          <label htmlFor="s-abs-folder">Folder</label>
          <select id="s-abs-folder" value={String(draft[FOLDER_KEY])} disabled={!libs || !libraryId} onChange={(e) => set(FOLDER_KEY, e.target.value)}>
            <option value="">None</option>
            {String(draft[FOLDER_KEY]) && !folders.some((f) => f.id === draft[FOLDER_KEY]) && <option value={String(draft[FOLDER_KEY])}>{String(draft[FOLDER_KEY])}</option>}
            {folders.map((f) => (
              <option key={f.id} value={f.id}>
                {f.path}
              </option>
            ))}
          </select>
          {errors[FOLDER_KEY] && (
            <div className="error small" role="alert">
              {errors[FOLDER_KEY]}
            </div>
          )}
        </div>
        <div className="field wide">
          <div className="row">
            <button type="button" disabled={!canList || loading} onClick={() => void loadLibs()}>
              {loading ? "Loading..." : "Refresh libraries"}
            </button>
            {libsError && (
              <span className="error small" role="alert">
                {libsError}
              </span>
            )}
          </div>
          <div className="muted small">
            {canList ? "Libraries come from the saved connection." : "Save a server URL and token to choose a library and folder."}
            {canList && unsavedConnection ? " You have unsaved connection changes; save before refreshing." : ""}
          </div>
        </div>
      </div>
    </section>
  );
}
