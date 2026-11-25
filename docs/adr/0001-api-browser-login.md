# ADR 0001: Account sign-in over the daemon API

Status: accepted.

## Context

A headless daemon (Docker, NAS) needs a way to add an Audible account or sign in again when
tokens expire, without a desktop app on the same machine. Amazon sign-in must happen in a real
browser; the daemon cannot host it.

## Decision

Reuse `libationcli login-external` as an API, via the shared `ExternalLoginFlow` and
`AudibleApi`'s `ILoginExternal` callback.

1. `POST /api/accounts/login {accountId?, locale, deviceRegistration?}` starts the flow in a
   background task. The library builds the Amazon sign-in URL (with its PKCE verifier, which
   never leaves the process) and calls the callback, which parks. The call returns
   `{loginId, url, expiresAt}`.
2. The user opens `url` in any browser, signs in, and copies the final address-bar URL (the page
   may say it does not exist).
3. `POST /api/accounts/login/{loginId} {responseUrl}` releases the parked callback. The tokens are
   saved through the CLI's path (encrypted with the master key) and the `AccountDto` is returned.
4. `DELETE /api/accounts/login/{loginId}` cancels.

A pending login is held in memory only, lasts 10 minutes, is single use, and at most 5 exist.
Starting another login for the same account replaces the earlier one.

## Threat model

- The response URL carries a one-time authorization code that, with the verifier held in the
  daemon, yields tokens. It is treated like a password: only read from the request body, never
  logged, echoed, put in an error or stored; responses are `Cache-Control: no-store`.
- Everything is refused with 403 unless an API token is configured (`--api-token-file`), so an
  unauthenticated or merely loopback-bound daemon cannot be made to sign in as someone else or
  have its account replaced.
- The login id is 128 random bits, and the shared bearer token gates every route; there are no
  scopes.
- Not defended: someone holding the bearer token can start logins and add their own Amazon
  account, as they can already change settings and download. A hostile browser extension on the
  user's machine can read the URL, as with the CLI.
- In import mode (`--accounts-import`, `--readonly-config`) a new account is refused (409): a
  changed import file would replace it. Signing in again for an account in the file is allowed.

## Rejected alternatives

- Username and password (or OTP/CAPTCHA relay) through the daemon: the password transits and
  sits in memory, CAPTCHA and device challenges cannot be solved headlessly, and it is the path
  Amazon is hardest on.
- A WebView or headless browser in the daemon: a large dependency and attack surface, still
  cannot solve challenges for the user, and does not exist in the slim image.
- Daemon-hosted redirect/callback endpoint: Amazon's redirect target for the Audible app client
  is fixed, so the code cannot be delivered to the daemon automatically.
- Persisting pending logins: would put the verifier on disk for no benefit; a restart simply
  means starting again.
- Per-route scopes for login: deferred; one shared token is the owner's decision.
