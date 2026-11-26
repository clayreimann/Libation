import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { BookDetail } from "../api/types";
import { ratingError, ratingFields, setField, toMyRating, type MyRating } from "../rating";

interface Props {
  asin: string;
  current: MyRating;
  onSaved: (b: BookDetail) => void;
}

export function RatingEditor({ asin, current, onSaved }: Props) {
  const [value, setValue] = useState<MyRating>(toMyRating(current));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setValue(toMyRating(current));
    setError(null);
  }, [asin, current.overall, current.performance, current.story]);

  const choose = async (key: keyof MyRating, stars: number) => {
    const previous = value;
    const next = setField(value, key, stars);
    const invalid = ratingError(next);
    if (invalid) {
      setError(invalid);
      return;
    }
    setValue(next);
    setSaving(true);
    setError(null);
    try {
      onSaved(await api.setRating(asin, next));
    } catch (e) {
      setValue(previous);
      setError(`Rating not saved: ${(e as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <h3>My rating</h3>
      <div className="rating-editor">
        {ratingFields.map((f) => (
          <div key={f.key} className="rating-row">
            <span>{f.label}</span>
            <span role="radiogroup" aria-label={`${f.label} rating`}>
              {[1, 2, 3, 4, 5].map((n) => (
                <button
                  key={n}
                  type="button"
                  role="radio"
                  aria-checked={value[f.key] === n}
                  aria-label={`${n} ${n === 1 ? "star" : "stars"}`}
                  className={`star ${n <= value[f.key] ? "on" : ""}`}
                  disabled={saving}
                  onClick={() => void choose(f.key, n)}
                >
                  {n <= value[f.key] ? "★" : "☆"}
                </button>
              ))}
            </span>
          </div>
        ))}
      </div>
      <p className={error ? "error" : "muted small"}>{error ?? (saving ? "Sending to Audible..." : "Sent to Audible when you pick a star; click the same star to clear it.")}</p>
    </>
  );
}
