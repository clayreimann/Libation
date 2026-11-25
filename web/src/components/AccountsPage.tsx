import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "../api/client";
import type { Account, LoginStart } from "../api/types";
import { LOCALES, accountKey, accountLabel, describeError, localeParam, reconcileNeedsLogin, scanTargets, type AccountOp } from "../accounts";
import { useLive } from "../live";
import { LoginCard } from "./LoginCard";

interface Msg {
  text: string;
  error: boolean;
}

interface LoginTarget {
  accountId: string;
  locale: string;
  label: string;
}

const fail = (op: AccountOp, e: unknown) => describeError(op, e instanceof ApiError ? e.status : 0, (e as Error).message);

function AuthBadge({ account }: { account: Account }) {
  return account.authState === "needsLogin" ? <span className="pill bad">needs sign-in</span> : <span className="pill ok">signed in</span>;
}

interface RowProps {
  account: Account;
  all: Account[];
  locked: boolean;
  selected: boolean;
  busy: boolean;
  onSelect: (on: boolean) => void;
  onChanged: (updated: Account | null, original: Account) => void;
  onLocked: () => void;
  onScan: (a: Account) => void;
  onSignIn: (a: Account) => void;
  message?: Msg;
}

function AccountRow({ account, all, locked, selected, busy, onSelect, onChanged, onLocked, onScan, onSignIn, message }: RowProps) {
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState("");
  const [confirming, setConfirming] = useState(false);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const guard = async (op: AccountOp, run: () => Promise<void>) => {
    setWorking(true);
    setError(null);
    try {
      await run();
    } catch (e) {
      const d = fail(op, e);
      if (d.importManaged) onLocked();
      setError(d.text);
    } finally {
      setWorking(false);
    }
  };

  const locale = localeParam(account, all);
  const save = () =>
    guard("patch", async () => {
      onChanged(await api.patchAccount(account.accountId, { accountName: name.trim() }, locale), account);
      setEditing(false);
    });
  const toggleScan = (on: boolean) => guard("patch", async () => onChanged(await api.patchAccount(account.accountId, { libraryScan: on }, locale), account));
  const remove = () =>
    guard("delete", async () => {
      await api.deleteAccount(account.accountId, locale);
      onChanged(null, account);
    });

  const disabled = working || busy;
  return (
    <tr className={selected ? "selected" : undefined}>
      <td className="check">
        <input type="checkbox" checked={selected} onChange={(e) => onSelect(e.target.checked)} aria-label={`Select ${accountLabel(account)}`} />
      </td>
      <td>
        {editing && !locked ? (
          <div className="row">
            <input
              type="text"
              value={name}
              aria-label="Account name"
              autoFocus
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && name.trim()) void save();
                if (e.key === "Escape") setEditing(false);
              }}
            />
            <button className="primary" disabled={disabled || !name.trim()} onClick={save}>
              Save
            </button>
            <button disabled={working} onClick={() => setEditing(false)}>
              Cancel
            </button>
          </div>
        ) : (
          <>
            <strong>{accountLabel(account)}</strong>
            {account.accountName && <div className="muted small">{account.accountId}</div>}
          </>
        )}
        {error && (
          <div className="error small" role="alert">
            {error}
          </div>
        )}
        {message && <div className={`small ${message.error ? "error" : "muted"}`}>{message.text}</div>}
        {confirming && (
          <div className="card attention">
            Remove <strong>{accountLabel(account)}</strong>? Its books stay in your library; only the saved sign-in is removed.
            <div className="row">
              <button className="primary" disabled={disabled} onClick={remove}>
                Remove account
              </button>
              <button disabled={working} onClick={() => setConfirming(false)}>
                Keep
              </button>
            </div>
          </div>
        )}
      </td>
      <td>
        {account.locale ?? "-"}
        {account.additionalLocales && account.additionalLocales.length > 0 && <div className="muted small">also {account.additionalLocales.join(", ")}</div>}
      </td>
      <td>
        <AuthBadge account={account} />
        <div className="muted small">{account.credentials === "none" ? "no credentials" : `${account.credentials} credentials`}</div>
      </td>
      <td>
        <label className="check-label">
          <input type="checkbox" checked={account.libraryScan} disabled={disabled || locked} onChange={(e) => toggleScan(e.target.checked)} /> scan library
        </label>
      </td>
      <td className="actions">
        <button disabled={disabled} onClick={() => onScan(account)}>
          Scan this account
        </button>
        <button className={account.authState === "needsLogin" ? "primary" : undefined} disabled={disabled} onClick={() => onSignIn(account)}>
          {account.authState === "needsLogin" ? "Sign in" : "Sign in again"}
        </button>
        <button
          disabled={disabled || locked}
          onClick={() => {
            setName(account.accountName ?? "");
            setEditing(true);
          }}
        >
          Rename
        </button>
        <button disabled={disabled || locked} onClick={() => setConfirming(true)}>
          Remove
        </button>
      </td>
    </tr>
  );
}

function AddAccount({ disabled, onStart }: { disabled: boolean; onStart: (t: LoginTarget) => void }) {
  const [accountId, setAccountId] = useState("");
  const [locale, setLocale] = useState<string>(LOCALES[0].name);
  return (
    <form
      className="card row"
      onSubmit={(e) => {
        e.preventDefault();
        onStart({ accountId: accountId.trim(), locale, label: accountId.trim() });
        setAccountId("");
      }}
    >
      <label>
        Login (email or phone) <input type="text" value={accountId} onChange={(e) => setAccountId(e.target.value)} autoComplete="off" />
      </label>
      <label>
        Marketplace{" "}
        <select value={locale} onChange={(e) => setLocale(e.target.value)} aria-label="Marketplace">
          {LOCALES.map((l) => (
            <option key={l.name} value={l.name}>
              {l.label} ({l.name})
            </option>
          ))}
        </select>
      </label>
      <button className="primary" type="submit" disabled={disabled || !accountId.trim()}>
        Add account
      </button>
    </form>
  );
}

export function AccountsPage() {
  const { status, setStatus, refreshStatus, accountEvents } = useLive();
  const [accounts, setAccounts] = useState<Account[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [locked, setLocked] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Msg | null>(null);
  const [rowMsg, setRowMsg] = useState<Record<string, Msg>>({});
  const [login, setLogin] = useState<{ start: LoginStart; target: LoginTarget } | null>(null);
  const [starting, setStarting] = useState(false);

  const statusRef = useRef(status);
  statusRef.current = status;

  const load = useCallback(
    () =>
      api.accounts().then(
        (list) => {
          setAccounts(list);
          setLoadError(null);
          const next = reconcileNeedsLogin(statusRef.current, list);
          if (next) setStatus(next);
        },
        (e: Error) => setLoadError(e.message),
      ),
    [setStatus],
  );

  useEffect(() => {
    void load();
  }, [load, accountEvents]);

  const apply = (updated: Account | null, original: Account) => {
    const next = (accounts ?? []).flatMap((a) => (accountKey(a) !== accountKey(original) ? [a] : updated ? [updated] : []));
    setAccounts(next);
    const flagged = reconcileNeedsLogin(statusRef.current, next);
    if (flagged) setStatus(flagged);
    if (!updated) setSelected((s) => new Set([...s].filter((k) => k !== accountKey(original))));
  };

  const scan = async (names: string[], label: string, key?: string) => {
    setBusy(true);
    setNotice(null);
    setRowMsg({});
    try {
      setStatus(await api.scan(names));
      const msg = { text: `Scan requested for ${label}.`, error: false };
      if (key) setRowMsg({ [key]: msg });
      else setNotice(msg);
    } catch (e) {
      const msg = { text: fail("scan", e).text, error: true };
      if (key) setRowMsg({ [key]: msg });
      else setNotice(msg);
    } finally {
      setBusy(false);
    }
  };

  const startLogin = async (target: LoginTarget) => {
    setStarting(true);
    setNotice(null);
    try {
      const start = await api.startLogin({ accountId: target.accountId, locale: target.locale });
      setLogin({ start, target });
    } catch (e) {
      setNotice({ text: fail("loginStart", e).text, error: true });
    } finally {
      setStarting(false);
    }
  };

  if (loadError) return <p className="error">{loadError}</p>;
  if (!accounts) return <p className="muted">Loading...</p>;

  const chosen = accounts.filter((a) => selected.has(accountKey(a)));
  return (
    <div className="accounts">
      {locked && (
        <p className="banner warn" role="status">
          Accounts are managed by import. Editing is disabled; change the imported accounts file and restart the daemon. Signing in again still works.
        </p>
      )}
      {notice && (
        <p className={notice.error ? "error" : "muted"} role={notice.error ? "alert" : "status"}>
          {notice.text}
        </p>
      )}
      {login && (
        <LoginCard
          key={login.start.loginId}
          login={login.start}
          label={login.target.label}
          onSignedIn={(account) => {
            setLogin(null);
            setNotice({ text: `Signed in as ${accountLabel(account)}.`, error: false });
            void load();
            refreshStatus();
          }}
          onClose={(restart) => {
            const target = login.target;
            setLogin(null);
            if (restart) void startLogin(target);
          }}
        />
      )}

      <div className="toolbar">
        <button disabled={busy || chosen.length === 0} onClick={() => scan(scanTargets(chosen), `${chosen.length} selected`)}>
          Scan selected{chosen.length > 0 ? ` (${chosen.length})` : ""}
        </button>
        <button disabled={busy || accounts.length === 0} onClick={() => scan(scanTargets(accounts), "all accounts")}>
          Scan all accounts
        </button>
      </div>

      {accounts.length === 0 ? (
        <p className="muted">No accounts yet. Add one below.</p>
      ) : (
        <div className="tablewrap">
          <table>
            <thead>
              <tr>
                <th className="check">
                  <input
                    type="checkbox"
                    aria-label="Select all"
                    checked={chosen.length === accounts.length}
                    onChange={(e) => setSelected(e.target.checked ? new Set(accounts.map(accountKey)) : new Set())}
                  />
                </th>
                <th>Account</th>
                <th>Marketplace</th>
                <th>Sign-in</th>
                <th>Scanning</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {accounts.map((a) => (
                <AccountRow
                  key={accountKey(a)}
                  account={a}
                  all={accounts}
                  locked={locked}
                  selected={selected.has(accountKey(a))}
                  busy={busy || starting}
                  message={rowMsg[accountKey(a)]}
                  onSelect={(on) =>
                    setSelected((s) => {
                      const next = new Set(s);
                      if (on) next.add(accountKey(a));
                      else next.delete(accountKey(a));
                      return next;
                    })
                  }
                  onChanged={apply}
                  onLocked={() => setLocked(true)}
                  onScan={(acc) => scan([acc.accountId], accountLabel(acc), accountKey(acc))}
                  onSignIn={(acc) => startLogin({ accountId: acc.accountId, locale: acc.locale ?? LOCALES[0].name, label: accountLabel(acc) })}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3>Add an account</h3>
      <AddAccount disabled={starting || !!login} onStart={startLogin} />
      <p className="muted small">Adding an account signs in through your browser. It needs the daemon's API token.</p>
    </div>
  );
}
