import type { AbsLibrary, AbsTest, AbsTestRequest } from "./api/types";

const CODE_TEXT: Record<string, string> = {
  badUrl: "That is not a valid http or https address.",
  unreachable: "The server could not be reached. Check the address and that the daemon can reach it.",
  unauthorized: "The server rejected the token.",
  serverError: "The server answered with an error.",
  notConfigured: "No server URL and token are saved yet; enter them to test.",
};

export interface Outcome {
  ok: boolean;
  text: string;
}

export function describeTest(r: AbsTest): Outcome {
  if (r.ok) return { ok: true, text: ["Connected", r.serverVersion && `server ${r.serverVersion}`, r.user && `as ${r.user}`].filter(Boolean).join(", ") + "." };
  const base = CODE_TEXT[r.code ?? ""];
  return { ok: false, text: base ? (r.error && r.error !== base ? `${base} (${r.error})` : base) : (r.error ?? "The connection test failed.") };
}

/** Test body from unsaved edits: URL only when it differs from the saved one, token only when typed. */
export function testRequest(savedUrl: string, url: string, token: string): AbsTestRequest {
  const body: AbsTestRequest = {};
  if (url.trim() && url.trim() !== savedUrl.trim()) body.url = url.trim();
  if (token.trim()) body.token = token.trim();
  return body;
}

export function describeLibrariesError(status: number, message: string, code?: string | null): string {
  if (status === 409) return "Save a server URL and token first; libraries are listed from the saved connection.";
  return code && CODE_TEXT[code] ? `${CODE_TEXT[code]} (${message})` : message;
}

export const foldersOf = (libs: readonly AbsLibrary[], libraryId: string) => libs.find((l) => l.id === libraryId)?.folders ?? [];

/** Folder to select after choosing a library: the only one, else the first, else none. */
export const defaultFolder = (libs: readonly AbsLibrary[], libraryId: string): string => foldersOf(libs, libraryId)[0]?.id ?? "";
