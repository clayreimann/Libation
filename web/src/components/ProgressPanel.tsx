import { useState } from "react";
import { api, ApiError } from "../api/client";
import type { BookProgress, ProgressPush } from "../api/types";
import { progressRows, pushErrorText, pushView, type PushView } from "../progress";

interface Props {
  asin: string;
  lengthMinutes: number;
  disabled: boolean;
}

export function ProgressPanel({ asin, lengthMinutes, disabled }: Props) {
  const [progress, setProgress] = useState<{ asin: string; value: BookProgress } | null>(null);
  const [progressError, setProgressError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [pushing, setPushing] = useState(false);
  const [result, setResult] = useState<{ asin: string; push: ProgressPush; view: PushView } | null>(null);
  const [pushError, setPushError] = useState<string | null>(null);

  const current = progress?.asin === asin ? progress.value : null;
  const shown = result?.asin === asin ? result : null;

  const load = async () => {
    setLoading(true);
    setProgressError(null);
    try {
      setProgress({ asin, value: await api.progress(asin) });
    } catch (e) {
      setProgressError(`Progress: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  };

  const push = async (force: boolean) => {
    setPushing(true);
    setPushError(null);
    try {
      const push = await api.pushProgress(asin, force);
      const view = pushView(push);
      setResult({ asin, push, view });
      if (view.kind === "newer" || view.kind === "ok") await load();
    } catch (e) {
      setResult(null);
      setPushError(e instanceof ApiError ? pushErrorText(e.status, e.message) : (e as Error).message);
    } finally {
      setPushing(false);
    }
  };

  return (
    <>
      <h3>Listening progress</h3>
      <div className="row">
        <button onClick={() => void load()} disabled={loading}>
          {loading ? "Reading..." : current ? "Refresh" : "Check progress"}
        </button>
        <button onClick={() => void push(false)} disabled={disabled || pushing} title="Sends Audible's position to Audiobookshelf now">
          {pushing ? "Pushing..." : "Push Audible progress to Audiobookshelf"}
        </button>
      </div>
      {progressError && <p className="error small">{progressError}</p>}
      {current && (
        <dl className="drawer-dl">
          {progressRows(current, lengthMinutes).map((r) => (
            <div key={r.label}>
              <dt>{r.label}</dt>
              <dd>{r.text}</dd>
            </div>
          ))}
        </dl>
      )}
      {pushError && <p className="error small">{pushError}</p>}
      {shown && shown.view.kind !== "newer" && <p className={shown.view.kind === "bad" ? "error small" : "muted small"}>{shown.view.text}</p>}
      {shown?.view.kind === "newer" && (
        <div className="card attention" role="alert">
          <p>Not pushed: Audiobookshelf has newer progress.</p>
          <p className="muted small">{shown.view.text}</p>
          {current ? (
            <ul className="plain small">
              {progressRows(current, lengthMinutes).map((r) => (
                <li key={r.label}>
                  {r.label}: {r.text}
                </li>
              ))}
            </ul>
          ) : (
            <p className="muted small">Use Check progress to compare both positions.</p>
          )}
          <button onClick={() => void push(true)} disabled={pushing}>
            Overwrite Audiobookshelf progress
          </button>
        </div>
      )}
    </>
  );
}
