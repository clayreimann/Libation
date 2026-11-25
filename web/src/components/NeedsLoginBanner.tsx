import { useLive } from "../live";

export function NeedsLoginBanner() {
  const { status } = useLive();
  const ids = status?.accountsNeedingLogin ?? [];
  if (ids.length === 0) return null;
  return (
    <div className="banner warn" role="alert">
      <span>
        {ids.length === 1 ? "1 account needs" : `${ids.length} accounts need`} to sign in again: <strong>{ids.join(", ")}</strong>. Scans and downloads for {ids.length === 1 ? "it" : "them"} will fail until then.
      </span>
      <a href="#/accounts">Open Accounts</a>
    </div>
  );
}
