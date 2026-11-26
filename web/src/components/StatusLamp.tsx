import type { Book } from "../api/types";
import { formatDate } from "../format";

export type LampState = "red" | "yellow" | "green" | "error" | "gone";

/** Same reading as the desktop stoplight: red not downloaded, yellow partial, green liberated. */
export function lampState(b: Book): LampState {
  if (b.bookStatus === "Liberated") return "green";
  if (b.bookStatus === "Error") return "error";
  if (b.absentFromLastScan) return "gone";
  if (b.partialDownload) return "yellow";
  return "red";
}

const describe: Record<LampState, string> = {
  red: "Not downloaded",
  yellow: "Partly downloaded",
  green: "Downloaded",
  error: "Download failed",
  gone: "Unavailable: no longer in the Audible library",
};

export const lampLabel = (b: Book): string => describe[lampState(b)];

export function StatusLamp({ book, onLiberate, disabled }: { book: Book; onLiberate?: () => void; disabled?: boolean }) {
  const state = lampState(book);
  const canLiberate = !!onLiberate && !book.inTrash && state !== "green" && state !== "gone";
  const parts = [describe[state]];
  if (book.pdfStatus) parts.push(book.pdfStatus === "Liberated" ? "PDF downloaded" : "PDF not downloaded");
  if (book.isAudiblePlus) parts.push(book.includedUntil ? `Audible Plus until ${formatDate(book.includedUntil)}` : "Audible Plus");
  if (canLiberate) parts.push("click to liberate");
  const title = parts.join(" · ");
  const body = (
    <>
      <span className={`lamp lamp-${state}`} aria-hidden="true">
        <i />
        <i />
        <i />
      </span>
      {book.pdfStatus && <span className={`lamp-pdf ${book.pdfStatus === "Liberated" ? "done" : "todo"}`} aria-hidden="true">PDF</span>}
      {book.isAudiblePlus && <span className="lamp-plus" aria-hidden="true">+</span>}
    </>
  );
  return canLiberate ? (
    <button
      className="lamp-wrap"
      title={title}
      aria-label={`${title}`}
      disabled={disabled}
      onClick={(e) => {
        e.stopPropagation();
        onLiberate!();
      }}
    >
      {body}
    </button>
  ) : (
    <span className="lamp-wrap" title={title} role="img" aria-label={title}>
      {body}
    </span>
  );
}
