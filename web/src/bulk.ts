export interface BulkResult<T> {
  ok: number;
  failed: { item: T; error: string }[];
  cancelled: boolean;
}

export interface BulkOptions<T> {
  concurrency?: number;
  signal?: AbortSignal;
  onProgress?: (done: number, total: number) => void;
  worker: (item: T) => Promise<unknown>;
}

/** Runs worker over items with bounded concurrency; failures are collected, never thrown. */
export async function runBulk<T>(items: readonly T[], { concurrency = 4, signal, onProgress, worker }: BulkOptions<T>): Promise<BulkResult<T>> {
  const result: BulkResult<T> = { ok: 0, failed: [], cancelled: false };
  let next = 0;
  let done = 0;

  const lane = async () => {
    while (next < items.length) {
      if (signal?.aborted) {
        result.cancelled = true;
        return;
      }
      const item = items[next++] as T;
      try {
        await worker(item);
        result.ok++;
      } catch (e) {
        result.failed.push({ item, error: e instanceof Error ? e.message : String(e) });
      }
      onProgress?.(++done, items.length);
    }
  };

  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, items.length)) }, lane));
  return result;
}

export const BULK_MAX = 5000;

export interface BulkItems {
  updated: number;
  missing: string[];
  results: { asin: string; result: string }[];
}

export interface BulkSummary {
  total: number;
  updated: number;
  conflict: string[];
  notFound: string[];
  failed: { asins: string[]; error: string }[];
  cancelled: boolean;
}

export interface ChunkOptions {
  signal?: AbortSignal;
  onProgress?: (done: number, total: number) => void;
  chunkSize?: number;
}

/** Posts asins in sequential chunks and merges the per-ASIN results; a failed request fails its whole chunk. */
export async function runChunked(asins: readonly string[], call: (chunk: string[]) => Promise<BulkItems>, o: ChunkOptions = {}): Promise<BulkSummary> {
  const size = o.chunkSize ?? BULK_MAX;
  const s: BulkSummary = { total: asins.length, updated: 0, conflict: [], notFound: [], failed: [], cancelled: false };
  let done = 0;
  for (let i = 0; i < asins.length; i += size) {
    if (o.signal?.aborted) {
      s.cancelled = true;
      break;
    }
    const chunk = asins.slice(i, i + size);
    try {
      const res = await call(chunk);
      s.updated += res.updated;
      for (const r of res.results) {
        if (r.result === "conflict") s.conflict.push(r.asin);
        else if (r.result === "notFound") s.notFound.push(r.asin);
      }
    } catch (e) {
      s.failed.push({ asins: chunk, error: e instanceof Error ? e.message : String(e) });
    }
    done += chunk.length;
    o.onProgress?.(done, asins.length);
  }
  return s;
}

/** ASINs that did not end up ok. */
export const notOk = (s: BulkSummary): string[] => [...s.conflict, ...s.notFound, ...s.failed.flatMap((f) => f.asins)];
