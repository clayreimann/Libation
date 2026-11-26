import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "../api/client";
import type { Account, LoginStart } from "../api/types";
import { countdown, describeError } from "../accounts";

interface Props {
  login: LoginStart;
  label: string;
  onSignedIn: (account: Account) => void;
  onClose: (restart?: boolean) => void;
}

/** Holds the pasted URL only in component state; it is never logged, stored or put in a URL. */
export function LoginCard({ login, label, onSignedIn, onClose }: Props) {
  const [responseUrl, setResponseUrl] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [restart, setRestart] = useState(false);
  const [now, setNow] = useState(Date.now());
  const mounted = useRef(true);
  const settled = useRef(false);

  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, []);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      window.setTimeout(() => {
        if (!mounted.current && !settled.current) void api.cancelLogin(login.loginId).catch(() => undefined);
      });
    };
  }, [login.loginId]);

  const left = countdown(login.expiresAt, now);
  const dead = restart || left.expired;

  const submit = async () => {
    setBusy(true);
    setMessage(null);
    try {
      const account = await api.completeLogin(login.loginId, responseUrl.trim());
      settled.current = true;
      setResponseUrl("");
      onSignedIn(account);
    } catch (e) {
      const d = describeError("loginComplete", e instanceof ApiError ? e.status : 0, (e as Error).message, e instanceof ApiError ? e.detail : {});
      if (d.restart) {
        settled.current = true;
        setResponseUrl("");
        setRestart(true);
      }
      setMessage(d.text);
    } finally {
      setBusy(false);
    }
  };

  const cancel = async () => {
    settled.current = true;
    setResponseUrl("");
    if (!dead) await api.cancelLogin(login.loginId).catch(() => undefined);
    onClose();
  };

  return (
    <section className="card login-card" aria-label={`Sign in ${label}`}>
      <div className="queue-head">
        <h3>Sign in: {label}</h3>
        <span className={left.expired ? "error" : "muted"} aria-live="off">
          {left.expired ? "Expired" : `Expires in ${left.label}`}
        </span>
      </div>
      <ol className="steps">
        <li>
          <a className="button primary" href={login.url} target="_blank" rel="noopener noreferrer">
            Open Amazon sign-in
          </a>
          <span className="muted small"> opens in a new tab</span>
        </li>
        <li>
          Sign in with your Amazon/Audible credentials. Afterwards the browser shows an error or blank page, such as "Looking for something? We're sorry. The Web address you entered is not a functioning page on our site". That is a successful sign-in. Copy the full address from the address bar.
        </li>
        <li>
          <label htmlFor="login-response">Paste the address here</label>
          <input
            id="login-response"
            type="password"
            autoComplete="off"
            spellCheck={false}
            value={responseUrl}
            disabled={busy || dead}
            onChange={(e) => setResponseUrl(e.target.value)}
            placeholder="https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=..."
          />
          <div className="muted small">It works like a password: it is hidden, sent only to the daemon, and never stored.</div>
        </li>
        <li>
          <button className="primary" disabled={busy || dead || responseUrl.trim() === ""} onClick={submit}>
            {busy ? "Signing in..." : "Submit"}
          </button>
        </li>
      </ol>
      {message && (
        <p className="error" role="alert">
          {message}
        </p>
      )}
      {left.expired && !restart && <p className="error">This sign-in expired. Start it again.</p>}
      <div className="row">
        {dead && (
          <button className="primary" onClick={() => onClose(true)}>
            Restart
          </button>
        )}
        <button onClick={cancel} disabled={busy}>
          {dead ? "Close" : "Cancel"}
        </button>
      </div>
    </section>
  );
}
