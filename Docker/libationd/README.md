# libationd

`LibationCli serve` runs scan then liberate on an interval, replacing a `scan; liberate; sleep` shell loop.
Build the image with `docker build --target libationd -t libationd .`; see `compose.example.yml`.

## Environment

| Variable | Meaning |
| --- | --- |
| `LIBATION_FILES_DIR` | LibationFiles dir (`/config`). A minimal `Settings.json` is created if missing. |
| `LIBATION_BOOKS_DIR` | `Books` for that new `Settings.json` (`/data`). |
| `LIBATION_MASTER_KEY_FILE` | Raw master key file. Create it with `LibationCli export-master-key`. |
| `LIBATION_MASTER_KEY` | Master key as base64, instead of the file. |
| `LIBATION_ACCOUNTS_IMPORT` | `AccountsSettings.json` copied into `/config` when its sha256 changes. Checked at start and every cycle. |
| `LIBATION_METRICS_FILE` | Prometheus textfile written atomically after every cycle. |
| `SLEEP_TIME` | Cycle interval: `1h`, `30m`, `90s`, `3600` (seconds) or `hh:mm:ss`. Default `1h`. |

Each has a flag: `--libationFiles`, `--accounts-import`, `--metrics-file`, `--interval`. Also `--listen` and `--reconcile-on-start`.

## Volumes

`/config` (rw, settings, database and refreshed tokens), `/secrets` (ro), `/metrics`, `/data` (books).

## Health

`GET /health` returns the cycle state as JSON; the image `HEALTHCHECK` runs `LibationCli health`.
It listens on `127.0.0.1:8080` by default. `--listen http://0.0.0.0:8080` plus a port mapping exposes it: status only, no auth.

## Upgrading from the loop stack

- Replace the loop script and `liberate.sh` entrypoint with this image and `serve`.
- Existing `/config` and `/db` contents: point `LIBATION_FILES_DIR` at the LibationFiles dir that holds `Settings.json`.
- Mount the vault's `AccountsSettings.json` at `LIBATION_ACCOUNTS_IMPORT`; the metrics file keeps its names and is re-read at start.
- `serve` exits 3 if `--reconcile-on-start` fails or the accounts file is malformed.
- `SLEEP_TIME=-1` (run once) is not supported; run `scan` / `liberate` directly.
