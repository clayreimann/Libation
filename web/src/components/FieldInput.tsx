import type { Draft, Field } from "../settingsForm";

export interface FieldProps {
  field: Field;
  draft: Draft;
  errors: Record<string, string>;
  disabled?: boolean;
  set: (key: string, v: string | boolean) => void;
}

export function FieldInput({ field, draft, errors, disabled, set }: FieldProps) {
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
