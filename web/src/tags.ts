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

export const joinTags = (tags: readonly string[]) => tags.join(" ");

export function addTags(current: string, add: string): string {
  return joinTags(normalizeTags(`${current} ${add}`));
}

export function removeTags(current: string, remove: string): string {
  const drop = new Set(normalizeTags(remove));
  return joinTags(normalizeTags(current).filter((t) => !drop.has(t)));
}
