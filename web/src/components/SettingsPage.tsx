import { useEffect, useMemo, useState } from "react";
import { api, ApiError } from "../api/client";
import type { Settings } from "../api/types";
import { GROUPS, FIXUP_DEPENDENTS, TEMPLATE_FIELDS, booksFixedByEnvironment, buildPatch, toDraft, type Draft, type Field } from "../settingsForm";
import { TemplateField } from "./TemplateField";

interface FieldProps {
  field: Field;
  draft: Draft;
  errors: Record<string, string>;
  disabled?: boolean;
  set: (key: string, v: string | boolean) => void;
}

function FieldInput({ field, draft, errors, disabled, set }: FieldProps) {
  const id = `s-${field.key}`;
  const error = errors[field.key];
  const value = draft[field.key];
  const err = error && (
    <div className="error small" role="alert">
      {error}
    </div>
  );
  if (field.kind === "bool") {
    return (
      <div className="field">
        <label className="check-label" htmlFor={id}>
          <input id={id} type="checkbox" checked={value === true} disabled={disabled} onChange={(e) => set(field.key, e.target.checked)} /> {field.label}
        </label>
        {field.hint && <div className="muted small">{field.hint}</div>}
        {err}
      </div>
    );
  }
  return (
    <div className="field">
      <label htmlFor={id}>{field.label}</label>
      {field.kind === "select" ? (
        <select id={id} value={String(value)} disabled={disabled} onChange={(e) => set(field.key, e.target.value)}>
          {!field.options!.includes(String(value)) && <option value={String(value)}>{String(value)}</option>}
          {field.options!.map((o) => (
            <option key={o} value={o}>
              {o}
            </option>
          ))}
        </select>
      ) : (
        <input id={id} type="text" inputMode={field.kind === "number" ? "numeric" : undefined} value={String(value)} disabled={disabled} aria-invalid={!!error} onChange={(e) => set(field.key, e.target.value)} />
      )}
      {field.hint && <div className="muted small">{field.hint}</div>}
      {err}
    </div>
  );
}

export function SettingsPage() {
  const [saved, setSaved] = useState<Draft | null>(null);
  const [draft, setDraft] = useState<Draft>({});
  const [loadError, setLoadError] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [restart, setRestart] = useState<string[]>([]);
  const [booksLocked, setBooksLocked] = useState(false);
  const [saving, setSaving] = useState(false);
  const [done, setDone] = useState(false);
  const [settings, setSettings] = useState<Settings | null>(null);

  const load = (s: Settings) => {
    const d = toDraft(s);
    setSettings(s);
    setSaved(d);
    setDraft(d);
  };

  useEffect(() => {
    api.settings().then(load, (e: Error) => setLoadError(e.message));
  }, []);

  const skip = useMemo(() => (booksLocked ? new Set(["books"]) : new Set<string>()), [booksLocked]);
  const built = useMemo(() => (saved ? buildPatch(saved, draft, skip) : null), [saved, draft, skip]);

  if (loadError) return <p className="error">{loadError}</p>;
  if (!saved || !settings || !built) return <p className="muted">Loading...</p>;

  const set = (key: string, v: string | boolean) => {
    setDraft((d) => ({ ...d, [key]: v }));
    setErrors((e) => {
      if (!(key in e)) return e;
      const { [key]: _, ...rest } = e;
      return rest;
    });
    setDone(false);
  };

  const dirty = built.changed.length > 0;
  const localErrors = built.errors;
  const shown = { ...localErrors, ...errors };

  const save = async () => {
    if (Object.keys(localErrors).length > 0) {
      setErrors(localErrors);
      return;
    }
    setSaving(true);
    setFormError(null);
    setErrors({});
    setDone(false);
    try {
      const res = await api.patchSettings(built.patch);
      load(res);
      setRestart(res.restartRequired ?? []);
      setDone(true);
    } catch (e) {
      if (e instanceof ApiError && e.status === 400 && e.detail.fields) {
        const locked = booksFixedByEnvironment(e.detail.fields);
        const { books: _, ...rest } = e.detail.fields;
        const fields = locked ? rest : e.detail.fields;
        if (locked) {
          setBooksLocked(true);
          setDraft((d) => ({ ...d, books: saved.books! }));
        }
        setErrors(fields);
        setFormError(Object.keys(fields).length > 0 ? e.message : null);
      } else {
        setFormError(e instanceof Error ? e.message : String(e));
      }
    } finally {
      setSaving(false);
    }
  };

  const fixupOff = draft["download.allowLibationFixup"] !== true;
  const { folders } = settings;

  return (
    <form
      className="settings"
      onSubmit={(e) => {
        e.preventDefault();
        if (dirty && !saving) void save();
      }}
    >
      <p>
        Accounts are managed on the <a href="#/accounts">Accounts</a> page. Changes apply to the next book, not to jobs already running.
      </p>

      <section>
        <h3>Folders</h3>
        <div className="fields">
          <div className="field wide">
            <label htmlFor="s-books">Books folder</label>
            <input id="s-books" type="text" value={String(draft.books)} readOnly={booksLocked} aria-invalid={!!shown.books && !booksLocked} onChange={(e) => set("books", e.target.value)} />
            {booksLocked && <div className="muted small">Fixed by the LIBATION_BOOKS_DIR environment variable on the daemon; change that instead.</div>}
            {!booksLocked && shown.books && (
              <div className="error small" role="alert">
                {shown.books}
              </div>
            )}
            {!folders.books.exists && <div className="warn-text small">This folder does not exist yet.</div>}
            {folders.books.exists && !folders.books.writable && <div className="error small">This folder is not writable.</div>}
          </div>
          <div className="field wide">
            <label htmlFor="s-inProgress">Working folder</label>
            <input id="s-inProgress" type="text" value={String(draft.inProgress)} aria-invalid={!!shown.inProgress} onChange={(e) => set("inProgress", e.target.value)} />
            <div className="muted small">Empty resets to the default.</div>
            {shown.inProgress && (
              <div className="error small" role="alert">
                {shown.inProgress}
              </div>
            )}
          </div>
        </div>
      </section>

      <section>
        <h3>File naming</h3>
        <div className="fields">
          {TEMPLATE_FIELDS.map((f) => (
            <TemplateField key={f.key} id={f.key} label={f.label} value={String(draft[f.key])} error={shown[f.key]} onChange={(v) => set(f.key, v)} />
          ))}
        </div>
      </section>

      {GROUPS.map((g) => (
        <section key={g.id}>
          <h3>{g.title}</h3>
          <div className="fields">
            {g.fields.map((f) => (
              <FieldInput key={f.key} field={f} draft={draft} errors={shown} disabled={fixupOff && FIXUP_DEPENDENTS.includes(f.key)} set={set} />
            ))}
          </div>
        </section>
      ))}

      <div className="savebar">
        <button type="submit" className="primary" disabled={!dirty || saving}>
          {saving ? "Saving..." : "Save"}
        </button>
        <button type="button" disabled={!dirty || saving} onClick={() => { setDraft(saved); setErrors({}); setFormError(null); }}>
          Discard changes
        </button>
        {done && !dirty && <span className="muted" role="status">Saved.</span>}
        {formError && (
          <span className="error" role="alert">
            {formError}
          </span>
        )}
        {restart.length > 0 && <span className="warn-text">Restart the daemon for: {restart.join(", ")}.</span>}
      </div>
    </form>
  );
}
