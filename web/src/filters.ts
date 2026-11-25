/** Lucene queries behind the counts bar; field names from SearchEngine.FieldIndexRules. */
export const countQueries = {
  liberated: "IsLiberated",
  notLiberated: "-IsLiberated AND -LiberatedError AND -AbsentFromLastScan",
  error: "LiberatedError",
  unavailable: "AbsentFromLastScan AND -IsLiberated",
  pdf: "HasPDF",
} as const;


export const HIDE_EPISODES = "-Episode";

export function withHideEpisodes(q: string, hide: boolean): string {
  const t = q.trim();
  if (!hide) return t;
  return t ? `(${t}) AND ${HIDE_EPISODES}` : HIDE_EPISODES;
}

const topDomain: Record<string, string> = {
  us: "com",
  uk: "co.uk",
  germany: "de",
  canada: "ca",
  france: "fr",
  australia: "com.au",
  italy: "it",
  spain: "es",
  japan: "co.jp",
  india: "in",
  brazil: "com.br",
};

export function audibleUrl(locale: string, asin: string): string {
  return `https://www.audible.${topDomain[locale.toLowerCase()] ?? "com"}/pd/${encodeURIComponent(asin)}`;
}

/** Exact series (and podcast, since episodes link to their parent's ASIN) by Audible series id. */
export const seriesQuery = (seriesAsin: string): string => `SeriesId:${seriesAsin}`;
