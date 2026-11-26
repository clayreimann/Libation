import { describe, expect, it } from "vitest";
import type { Account, Status } from "./api/types";
import { addNeedsLogin, countdown, describeError, localeParam, needsLoginIds, reconcileNeedsLogin, scanTargets } from "./accounts";

const acct = (accountId: string, locale: string, authState: Account["authState"] = "signedIn"): Account => ({
  accountId,
  accountName: accountId,
  locale,
  libraryScan: true,
  authState,
  credentials: authState === "signedIn" ? "modern" : "none",
  additionalLocales: [],
});

const status = (ids: string[]): Status => ({ state: "idle", intervalSeconds: 60, accountsNeedingLogin: ids, accountsManagedByImport: false });

describe("needs-login reduction", () => {
  it("adds an id once from an event", () => {
    const s = addNeedsLogin(status([]), "a");
    expect(s?.accountsNeedingLogin).toEqual(["a"]);
    expect(addNeedsLogin(s, "a")).toBe(s);
    expect(addNeedsLogin(null, "a")).toBeNull();
  });

  it("clears ids whose account is signedIn and adds new needsLogin ones", () => {
    const s = reconcileNeedsLogin(status(["a", "b"]), [acct("a", "us"), acct("b", "us", "needsLogin"), acct("c", "uk", "needsLogin")]);
    expect(s?.accountsNeedingLogin).toEqual(["b", "c"]);
  });

  it("keeps ids of accounts the list does not mention and returns the same object when unchanged", () => {
    const base = status(["gone", "b"]);
    expect(reconcileNeedsLogin(base, [acct("b", "us", "needsLogin")])).toBe(base);
  });

  it("collects distinct ids", () => {
    expect(needsLoginIds([acct("a", "us", "needsLogin"), acct("a", "uk", "needsLogin"), acct("b", "us")])).toEqual(["a"]);
  });
});

describe("account helpers", () => {
  it("passes a locale only for ids shared by several marketplaces", () => {
    const all = [acct("t", "us"), acct("t", "canada"), acct("s", "us")];
    expect(localeParam(all[1]!, all)).toBe("canada");
    expect(localeParam(all[2]!, all)).toBeUndefined();
  });

  it("dedupes scan targets", () => {
    expect(scanTargets([acct("t", "us"), acct("t", "canada"), acct("s", "us")])).toEqual(["t", "s"]);
  });
});

describe("describeError", () => {
  it("flags import-managed 409s and disables editing", () => {
    const d = describeError("patch", 409, "Accounts are managed by the imported accounts file; edit it and restart");
    expect(d.importManaged).toBe(true);
    expect(d.text).toMatch(/managed by import/);
  });

  it("does not lock editing for a 409 on login start", () => {
    const d = describeError("loginStart", 409, "Accounts are managed by the imported accounts file; this account is not in it");
    expect(d.importManaged).toBe(false);
    expect(d.text).toMatch(/managed by import/);
  });

  it("explains an ambiguous id and passes other 409s through", () => {
    expect(describeError("patch", 409, "Several accounts share that id; add ?locale= to pick one.").text).toMatch(/more than one marketplace/);
    expect(describeError("delete", 409, "Accounts cannot be saved: denied").text).toBe("Accounts cannot be saved: denied");
  });

  it("maps 403, 429 and 502 on login", () => {
    expect(describeError("loginStart", 403, "x").text).toMatch(/API token/);
    expect(describeError("loginStart", 429, "x").text).toMatch(/at most 5/);
    expect(describeError("loginStart", 502, "x").text).toMatch(/Audible could not be reached/);
  });

  it("keeps the login open for a not-final URL and restarts otherwise", () => {
    const keep = describeError("loginComplete", 400, "That is not the final sign-in URL; copy the full address.");
    expect(keep.restart).toBe(false);
    expect(keep.text).toMatch(/not the final/);
    expect(describeError("loginComplete", 400, "Audible did not accept that sign-in; start a new login").restart).toBe(true);
    expect(describeError("loginComplete", 404, "No such pending login").restart).toBe(true);
  });

  it("passes plain 400 and 404 messages through for edits", () => {
    expect(describeError("patch", 400, "accountName must not be blank.").text).toBe("accountName must not be blank.");
    expect(describeError("patch", 404, "No such account.").text).toMatch(/no longer exists/);
  });
});

describe("countdown", () => {
  const end = "2026-01-01T00:10:00Z";
  const at = (s: string) => Date.parse(s);

  it("formats m:ss and rounds up", () => {
    expect(countdown(end, at("2026-01-01T00:00:00Z"))).toEqual({ expired: false, label: "10:00" });
    expect(countdown(end, at("2026-01-01T00:09:54.500Z"))).toEqual({ expired: false, label: "0:06" });
  });

  it("expires at and after the deadline, and on a bad date", () => {
    expect(countdown(end, at(end)).expired).toBe(true);
    expect(countdown(end, at("2026-01-02T00:00:00Z")).label).toBe("0:00");
    expect(countdown("nope", 0).expired).toBe(true);
  });
});
