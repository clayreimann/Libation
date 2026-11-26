import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { TemplatePreview } from "../api/types";
import { TEMPLATE_KINDS } from "../settingsForm";

interface Props {
  id: keyof typeof TEMPLATE_KINDS;
  label: string;
  value: string;
  error?: string;
  onChange: (v: string) => void;
}

export function TemplateField({ id, label, value, error, onChange }: Props) {
  const [preview, setPreview] = useState<TemplatePreview | null>(null);
  const [failed, setFailed] = useState<string | null>(null);

  useEffect(() => {
    if (!value.trim()) {
      setPreview(null);
      setFailed(null);
      return;
    }
    const ctl = new AbortController();
    const t = window.setTimeout(() => {
      api.previewTemplate({ template: value, kind: TEMPLATE_KINDS[id] }, ctl.signal).then(
        (p) => {
          setPreview(p);
          setFailed(null);
        },
        (e: Error) => !ctl.signal.aborted && setFailed(e.message),
      );
    }, 350);
    return () => {
      window.clearTimeout(t);
      ctl.abort();
    };
  }, [id, value]);

  return (
    <div className="field wide">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="text" value={value} spellCheck={false} aria-invalid={!!error} onChange={(e) => onChange(e.target.value)} />
      {error && (
        <div className="error small" role="alert">
          {error}
        </div>
      )}
      {failed && <div className="error small">Preview unavailable: {failed}</div>}
      {preview?.valid && preview.preview != null && (
        <div className="small">
          <span className="muted">Example: </span>
          <code>{preview.preview}</code>
        </div>
      )}
      {preview && !preview.valid && preview.errors.map((m) => (
        <div key={m} className="error small">
          {m}
        </div>
      ))}
      {preview?.warnings.map((m) => (
        <div key={m} className="small warn-text">
          {m}
        </div>
      ))}
    </div>
  );
}
