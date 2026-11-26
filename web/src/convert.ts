import type { AudioFile, Book } from "./api/types";

const ext = (name: string) => name.slice(name.lastIndexOf(".")).toLowerCase();

/** Mirrors the daemon's rule: liberated product, still in the last scan, an .m4b with no .mp3 beside it. */
export function canConvertToMp3(b: Pick<Book, "bookStatus" | "isPodcastEpisode" | "absentFromLastScan" | "inTrash">, files: readonly Pick<AudioFile, "name">[]): boolean {
  if (b.bookStatus !== "Liberated" || b.isPodcastEpisode || b.absentFromLastScan || b.inTrash) return false;
  const exts = files.map((f) => ext(f.name));
  return exts.includes(".m4b") && !exts.includes(".mp3");
}

export function convertOutcome(result: string | undefined): { text: string; bad: boolean; queue: boolean } {
  if (result === "ok") return { text: "Conversion to MP3 queued.", bad: false, queue: true };
  if (result === "conflict") return { text: "Not queued: already converted, not liberated, a podcast episode, absent from the last scan, or already has a job.", bad: true, queue: false };
  return { text: "Not queued: the daemon does not know this book.", bad: true, queue: false };
}
