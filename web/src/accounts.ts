import type { Account, Status } from "./api/types";

export const LOCALES = [
  { name: "us", label: "United States" },
  { name: "uk", label: "United Kingdom" },
  { name: "australia", label: "Australia" },
  { name: "brazil", label: "Brazil" },
  { name: "canada", label: "Canada" },
  { name: "france", label: "France" },
  { name: "germany", label: "Germany" },
  { name: "india", label: "India" },
  { name: "italy", label: "Italy" },
  { name: "japan", label: "Japan" },
  { name: "spain", label: "Spain" },
] as const;

export const accountKey = (a: Pick<Account, "accountId" | "locale">) => `${a.accountId}|${a.locale ?? ""}`;

export const accountLabel = (a: Pick<Account, "accountId" | "accountName">) => a.accountName || a.accountId;

/** The API needs ?locale= only when one login id is registered in several marketplaces. */
export function localeParam(account: Account, all: readonly Account[]): string | undefined {
  const twins = all.filter((a) => a.accountId.toLowerCase() === account.accountId.toLowerCase()).length;
  return twins > 1 && account.locale ? account.locale : undefined;
}

/** Distinct login ids, for POST /api/scan. */
export function scanTargets(accounts: readonly Account[]): string[] {
  return [...new Set(accounts.map((a) => a.accountId))];
}

export function needsLoginIds(accounts: readonly Account[]): string[] {
  return [...new Set(accounts.filter((a) => a.authState === "needsLogin").map((a) => a.accountId))];
}

export function addNeedsLogin(status: Status | null, accountId: string): Status | null {
  if (!status) return status;
  const ids = status.accountsNeedingLogin ?? [];
  return ids.includes(accountId) ? status : { ...status, accountsNeedingLogin: [...ids, accountId] };
}

/** Replaces the flagged ids with what a fresh account list says; ids of unlisted accounts are kept. */
export function reconcileNeedsLogin(status: Status | null, accounts: readonly Account[]): Status | null {
  if (!status) return status;
  const listed = new Set(accounts.map((a) => a.accountId));
  const next = [...(status.accountsNeedingLogin ?? []).filter((id) => !listed.has(id)), ...needsLoginIds(accounts)];
  const ids = [...new Set(next)];
  const current = status.accountsNeedingLogin ?? [];
  return ids.length === current.length && ids.every((id, i) => id === current[i]) ? status : { ...status, accountsNeedingLogin: ids };
}

export type AccountOp = "patch" | "delete" | "scan" | "loginStart" | "loginComplete" | "loginCancel";

export interface Described {
  text: string;
  restart: boolean;
  importManaged: boolean;
}

const API_TOKEN_HINT = "Browser sign-in needs the daemon's API token configured: start the daemon with --api-token-file and give the UI the same token.";

/** Turns an API failure into a message; `restart` means the login cannot be retried and must be started again. */
export function describeError(op: AccountOp, status: number, message: string): Described {
  const out = (text: string, extra: Partial<Described> = {}): Described => ({ text, restart: false, importManaged: false, ...extra });
  const login = op === "loginStart" || op === "loginComplete" || op === "loginCancel";

  if (status === 403 && login) return out(API_TOKEN_HINT);
  if (status === 429) return out("Too many sign-ins are waiting (at most 5). Finish or cancel one, then try again.");
  if (status === 502) return out("Audible could not be reached to start the sign-in. Check the daemon's network connection and try again.");

  if (status === 409 && /imported accounts file/i.test(message)) {
    return out(
      op === "loginStart"
        ? "Accounts are managed by import, so a new account cannot be added here. Signing in again for an account in the imported file still works."
        : "Accounts are managed by import: edit the imported accounts file and restart the daemon.",
      { importManaged: op !== "loginStart" },
    );
  }
  if (status === 409 && /\?locale=/.test(message)) return out("That login is registered in more than one marketplace; the account could not be chosen. Refresh the list and try again.");

  if (op === "loginComplete") {
    if (status === 404) return out("This sign-in expired or was already used. Start it again.", { restart: true });
    if (status === 400) {
      return /not the final/i.test(message) ? out(message) : out("Audible did not accept that sign-in. Start a new one and try again.", { restart: true });
    }
  }
  if ((op === "loginCancel" || op === "patch" || op === "delete") && status === 404) {
    return out(op === "loginCancel" ? "That sign-in is already gone." : "That account no longer exists. Refresh the list.");
  }
  return out(message);
}

export interface Countdown {
  expired: boolean;
  label: string;
}

/** Remaining time until `expiresAt` as m:ss. */
export function countdown(expiresAt: string, now: number): Countdown {
  const end = Date.parse(expiresAt);
  const ms = Number.isNaN(end) ? 0 : end - now;
  if (ms <= 0) return { expired: true, label: "0:00" };
  const total = Math.ceil(ms / 1000);
  return { expired: false, label: `${Math.floor(total / 60)}:${String(total % 60).padStart(2, "0")}` };
}
