/** Mirrors UserDefinedItem.sanitize: lower case, hyphen to underscore, letters/digits/underscore, unique, sorted. */
export function normalizeTags(input: string): string[] {
  const words = input
    .trim()
    .toLowerCase()
    .replace(/-/g, "_")
    .replace(/[^\p{L}\p{N}_\s]/gu, " ")
    .split(/\s+/)
    .filter(Boolean);
  return [...new Set(words)].sort();
}
