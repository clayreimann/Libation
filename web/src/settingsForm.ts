import type { Settings, SettingsPatch } from "./api/types";

export type Draft = Record<string, string | boolean>;

export type FieldKind = "text" | "bool" | "number" | "select";

export interface Field {
  key: string;
  label: string;
  kind: FieldKind;
  options?: readonly string[];
  hint?: string;
}

export interface Group {
  id: string;
  title: string;
  fields: Field[];
}

const bool = (key: string, label: string, hint?: string): Field => ({ key, label, kind: "bool", hint });
const select = (key: string, label: string, options: readonly string[]): Field => ({ key, label, kind: "select", options });
const num = (key: string, label: string, hint?: string): Field => ({ key, label, kind: "number", hint });

export const TEMPLATE_KINDS = {
  "naming.folderTemplate": "folder",
  "naming.fileTemplate": "file",
  "naming.chapterFileTemplate": "chapterFile",
  "naming.chapterTitleTemplate": "chapterTitle",
} as const;

export const TEMPLATE_FIELDS: { key: keyof typeof TEMPLATE_KINDS; label: string }[] = [
  { key: "naming.folderTemplate", label: "Folder template" },
  { key: "naming.fileTemplate", label: "File template" },
  { key: "naming.chapterFileTemplate", label: "Chapter file template" },
  { key: "naming.chapterTitleTemplate", label: "Chapter title template" },
];

export const FIXUP_DEPENDENTS = ["download.splitFilesByChapter", "download.stripUnabridged", "download.stripAudibleBrandAudio", "download.decryptToLossy"];

export const GROUPS: Group[] = [
  {
    id: "download",
    title: "Download",
    fields: [
      select("download.quality", "Quality", ["High", "Normal"]),
      select("download.spatialAudioCodec", "Spatial audio codec", ["EC_3", "AC_4"]),
      bool("download.useWidevine", "Use Widevine"),
      bool("download.requestXheAac", "Request xHE-AAC"),
      bool("download.overwriteExisting", "Overwrite existing files"),
      bool("download.downloadCoverArt", "Download cover art"),
      bool("download.useCoverAsFolderIcon", "Use cover as folder icon"),
      bool("download.downloadClipsBookmarks", "Download clips and bookmarks"),
      select("download.clipsBookmarksFileFormat", "Clips and bookmarks format", ["CSV", "Xlsx", "Json"]),
      bool("download.createCueSheet", "Create cue sheet"),
      bool("download.saveMetadataToFile", "Save metadata to file"),
      bool("download.retainAaxFile", "Keep the encrypted AAX file"),
      bool("download.mergeOpeningAndEndCredits", "Merge opening and end credits"),
      bool("download.moveMoovToBeginning", "Move moov atom to the beginning"),
      bool("download.combineNestedChapterTitles", "Combine nested chapter titles"),
      bool("download.allowLibationFixup", "Allow Libation fixup"),
      bool("download.splitFilesByChapter", "Split files by chapter", "Needs Libation fixup."),
      bool("download.stripUnabridged", "Strip \"Unabridged\" from titles", "Needs Libation fixup."),
      bool("download.stripAudibleBrandAudio", "Strip Audible branding audio", "Needs Libation fixup."),
      bool("download.decryptToLossy", "Decrypt to lossy MP3", "Needs Libation fixup."),
      num("download.minimumFileDuration", "Minimum file duration (seconds)", "0 to 120."),
      select("download.creationTime", "File creation time", ["File", "Published", "Added"]),
      select("download.lastWriteTime", "File last-write time", ["File", "Published", "Added"]),
    ],
  },
  {
    id: "mp3",
    title: "MP3 conversion",
    fields: [
      bool("mp3.targetBitrate", "Target a bitrate (otherwise VBR quality)"),
      num("mp3.bitrate", "Bitrate (kbps)", "16 to 320."),
      bool("mp3.constantBitrate", "Constant bitrate"),
      bool("mp3.matchSourceBitrate", "Match source bitrate"),
      num("mp3.vbrQuality", "VBR quality", "0 (best) to 9."),
      bool("mp3.downsampleMono", "Downsample mono sources"),
      select("mp3.encoderQuality", "Encoder quality", ["High", "Standard", "Fast"]),
      num("mp3.maxSampleRate", "Max sample rate (Hz)"),
    ],
  },
  {
    id: "queue",
    title: "Queue",
    fields: [
      select("queue.dailyDownloadLimit", "Daily download limit", ["NoLimit", "PlusOnly", "AllBooks"]),
      num("queue.dailyDownloadLimitQuantity", "Daily limit quantity"),
      select("queue.dailyDownloadLimitUnit", "Daily limit unit", ["Books", "MB", "GB"]),
      select("queue.badBook", "When a book fails", ["Ask", "Abort", "Retry", "Ignore"]),
    ],
  },
  {
    id: "library",
    title: "Library",
    fields: [
      bool("library.importEpisodes", "Import podcast episodes"),
      bool("library.importPlusTitles", "Import Audible Plus titles"),
      bool("library.downloadEpisodes", "Download episodes"),
      bool("library.autoDownloadEpisodes", "Download new episodes automatically"),
      bool("library.savePodcastsToParentFolder", "Save podcasts to the parent folder"),
    ],
  },
  {
    id: "logging",
    title: "Logging",
    fields: [select("logLevel", "Log level", ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"])],
  },
];

export const ABS_FIELDS: Field[] = [
  bool("audiobookshelf.enabled", "Upload automatically after each liberation"),
  bool("audiobookshelf.includePdfs", "Upload PDFs too"),
  bool("audiobookshelf.pushProgressAfterUpload", "Push Audible listening progress after each upload"),
];

export const ABS_TOKEN = "audiobookshelf.apiToken";

const NULL_WHEN_EMPTY = new Set(["inProgress", "audiobookshelf.serverUrl", "audiobookshelf.libraryId", "audiobookshelf.folderId"]);

export const TEXT_KEYS = ["books", "inProgress", ...TEMPLATE_FIELDS.map((f) => f.key), "audiobookshelf.serverUrl", ABS_TOKEN, "audiobookshelf.libraryId", "audiobookshelf.folderId"];

export const allFields = (groups: readonly Group[] = GROUPS): Field[] => [...groups.flatMap((g) => g.fields), ...ABS_FIELDS];

const NUMERIC = new Set(allFields().filter((f) => f.kind === "number").map((f) => f.key));

function dig(obj: unknown, key: string): unknown {
  return key.split(".").reduce<unknown>((o, k) => (o && typeof o === "object" ? (o as Record<string, unknown>)[k] : undefined), obj);
}

/** Editable values of a settings response keyed by the API's dotted field names; numbers become strings. */
export function toDraft(s: Settings): Draft {
  const d: Draft = {};
  for (const key of TEXT_KEYS) d[key] = String(dig(s, key) ?? "");
  for (const f of allFields()) {
    const v = dig(s, f.key);
    d[f.key] = f.kind === "bool" ? v === true : String(v ?? "");
  }
  return d;
}

export interface Built {
  patch: SettingsPatch;
  errors: Record<string, string>;
  changed: string[];
}

/** Nested PATCH body of only the keys whose draft differs from the saved one. */
export function buildPatch(saved: Draft, draft: Draft, skip: ReadonlySet<string> = new Set()): Built {
  const patch: Record<string, unknown> = {};
  const errors: Record<string, string> = {};
  const changed: string[] = [];
  for (const [key, value] of Object.entries(draft)) {
    if (skip.has(key) || saved[key] === value) continue;
    let out: unknown = value;
    if (NUMERIC.has(key)) {
      const text = String(value).trim();
      if (!/^-?\d+$/.test(text)) {
        errors[key] = "Must be a whole number.";
        continue;
      }
      out = Number(text);
    } else if (typeof value === "string" && TEXT_KEYS.includes(key)) {
      out = value.trim() === "" && NULL_WHEN_EMPTY.has(key) ? null : value.trim();
    }
    changed.push(key);
    const path = key.split(".");
    let node = patch;
    for (const part of path.slice(0, -1)) node = (node[part] ??= {}) as Record<string, unknown>;
    node[path[path.length - 1]!] = out;
  }
  return { patch: patch as SettingsPatch, errors, changed };
}

export const isDirty = (saved: Draft, draft: Draft, skip?: ReadonlySet<string>): boolean => {
  const b = buildPatch(saved, draft, skip);
  return b.changed.length > 0 || Object.keys(b.errors).length > 0;
};

/** True when the daemon refuses `books` because LIBATION_BOOKS_DIR sets it. */
export const booksFixedByEnvironment = (fields: Record<string, string> | undefined): boolean => /LIBATION_BOOKS_DIR/.test(fields?.books ?? "");

/** Adds `apiToken: null` to the patch when the saved token should be cleared and no new one was typed. */
export function withTokenClear(built: Built, clear: boolean): Built {
  if (!clear || built.changed.includes(ABS_TOKEN)) return built;
  const patch = { ...built.patch, audiobookshelf: { ...built.patch.audiobookshelf, apiToken: null } };
  return { ...built, patch, changed: [...built.changed, ABS_TOKEN] };
}
