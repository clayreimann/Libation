/** Worker return value that counts the item as skipped, not ok or failed. */
export const SKIPPED = Symbol("skipped");

export interface BulkResult<T> {
  ok: number;
  skipped: number;
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
  const result: BulkResult<T> = { ok: 0, skipped: 0, failed: [], cancelled: false };
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
        if ((await worker(item)) === SKIPPED) result.skipped++;
        else result.ok++;
      } catch (e) {
        result.failed.push({ item, error: e instanceof Error ? e.message : String(e) });
      }
      onProgress?.(++done, items.length);
    }
  };

  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, items.length)) }, lane));
  return result;
}
