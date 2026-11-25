import { useEffect, useState } from "react";
import { api } from "../api/client";

interface Props {
  asin: string;
  pictureId: string | null;
  size: "small" | "large";
}

export function Cover({ asin, pictureId, size }: Props) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [asin, pictureId]);

  if (!pictureId || failed) return <div className={`cover ${size} placeholder`} aria-hidden="true" />;
  return <img className={`cover ${size}`} src={api.coverUrl(asin, size)} alt="" loading="lazy" onError={() => setFailed(true)} />;
}
