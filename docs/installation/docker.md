# Docker Support

> [!WARNING] Breaking Changes
>
> The image now runs `LibationCli serve` directly instead of a shell script. See [Upgrading from the shell-script image](#upgrading-from-the-shell-script-image) for the changes that may affect your setup.

## Disclaimer

The docker image is provided as-is. We hope it can be useful to you but it is not officially supported.

## Configuration

> [!WARNING] Encrypted tokens need the master key in Docker
>
> If Docker logs show `Failed to decrypt ExistingAccessToken` (or your scan finds no books after copying Windows config), you copied an `AccountsSettings.json` whose auth tokens are encrypted with a key that normally stays on the desktop OS secret store.
>
> **Preferred fix (keep encryption):** On the desktop machine, export the master key:
>
> - **GUI:** Settings -> Important -> **Export encryption key...**
> - **CLI:** `LibationCli export-master-key libation-master.key`
>
> Copy `libation-master.key` into the same Docker config folder as `AccountsSettings.json` (mounted at `/config`). Libation loads it automatically. Alternatively set `LIBATION_MASTER_KEY_FILE` to the raw key file path, or `LIBATION_MASTER_KEY` to the Base64-encoded key. Treat the key file like a password.
>
> **Other fixes:** On Windows, **Settings -> Important**, uncheck **Store authentication tokens encrypted**, convert existing tokens to plaintext when prompted, then re-copy `AccountsSettings.json`. Or skip copying Windows accounts and use `login-external` / `import-account` inside the container (see below).
>
> **`serve` refuses to start** when `AccountsSettings.json` has encrypted tokens and no master key is supplied (no `LIBATION_MASTER_KEY_FILE`, `libation-master.key`, `LIBATION_MASTER_KEY`, or OS secret store), or when a supplied key file is unreadable or invalid. It exits with an error instead of creating a new key.
>
> **Last resort (not recommended):** For other commands, if encryption stays on and you never supply a key, Libation may auto-create `libation-master.key` under the Libation files directory and warn loudly (console, log, and `libation-master.key.NOTICE.txt`). That key only unlocks tokens encrypted with it afterward - it will not decrypt tokens from another machine. Prefer the steps above. When creating accounts in Docker, use `--libationFiles /config` so any key file persists on the host mount.
>
> **Encrypt-on-save without a key:** If tokens are already usable (plaintext, or ciphertext you can unlock) but encryption is preferred and no protector is available, a later save (for example after an access-token refresh) does **not** stop the container. Libation logs an error such as `Failed to encrypt identity field ... Saving as plaintext so the app can continue` and re-saves those fields as plaintext. That is intentional so headless Docker keeps working. Prefer supplying a master key (above) if you want tokens encrypted at rest. This does **not** fix `Failed to decrypt` for tokens encrypted on another machine without the key.
>
> Details: [FAQ](/docs/frequently-asked-questions#docker-finds-no-new-books-failed-to-decrypt-existingaccesstoken) · [Troubleshooting](/docs/advanced/troubleshoot#failed-to-decrypt-existingaccesstoken-docker-finds-no-new-books)

> [!WARNING] NTFS filesystem limitations
>
> NTFS filesystems (Windows, and NTFS-formatted external drives on Linux/Mac) do not support colons (`:`) in filenames. Since many audiobook titles contain colons (e.g., "Title: A Subtitle"), downloads may produce invalid filenames.
>
> **Solution:** Configure custom replacement characters in `Settings.json` to replace colons with compatible characters. See [Command Line Interface - Set custom replacement characters](/docs/advanced/command-line-interface#set-custom-replacement-characters) for configuration examples.

> [!TIP] Pacing downloads
>
> To stop a scheduled container from downloading your whole library at once, see [Daily download limit](/docs/features/daily-download-limit#docker-and-the-command-line). It is off unless you add the keys to your mounted `Settings.json`. Download counts live in Libation's database, so they survive container restarts when that database is on a volume.

Configuration in Libation is handled by two files, `AccountsSettings.json` and `Settings.json`. These files can usually be found in the Libation folder in your user's home directory. The easiest way to configure these is to run the desktop version of Libation and then copy them into a folder, such as `/opt/libation/config`, that you'll volume mount into the image. `Settings.json` is technically optional, and, if not provided, Libation will run using the default settings. The `Books` folder is taken from `LIBATION_BOOKS_DIR` (default `/data`) when set, overriding `Settings.json`. If tokens in that file are encrypted on the desktop, also copy the exported `libation-master.key` (see the encrypted-tokens warning above).

### Adding Audible accounts without the GUI

If you run Libation on a server or in Docker and do not want to copy `AccountsSettings.json` from a desktop install, you can create or update accounts with LibationCli (same binary as in the image under `/libation/LibationCli`, also available on `PATH` as `LibationCli`):

- `import-account` — Import an account from a JSON file exported by [mkb79's audible-cli](https://github.com/mkb79/audible-cli) (or Libation's own compatible export). Example:
  `LibationCli import-account /path/to/account.json`
- `login-external` — Browser-based sign-in: the CLI prints an Audible login URL; you open it in a normal browser, sign in, then paste the final URL from the address bar back into the terminal. Example:
  `LibationCli login-external --account you@example.com --locale us`
  If standard input is not a TTY (for example in some automation), pass the final URL with `--response-url "https://..."` instead of pasting interactively.
  Optional `--device-registration CurrentAndroid|Mkb79IPhone` chooses which virtual device to register. Android is the default and supports Widevine; iPhone is experimental and does not. See [Device registration](/docs/advanced/device-registration).
- `list-accounts` — List configured accounts and whether each has valid stored credentials (and scan-on/off). Example:
  `LibationCli list-accounts` or `LibationCli list-accounts --bare` for tab-separated output.

For full syntax, overrides, and the `--libationFiles` option (or the `LIBATION_FILES_DIR` environment variable) when your Libation data directory is not the default, see [Command Line Interface](/docs/advanced/command-line-interface).

Docker tip: The image's Libation files directory is `/config`, so LibationCli commands run in the container already use your mounted volume:

```bash
docker exec -it libation LibationCli login-external --locale us --account you@example.com
```

You can also start a container with an interactive shell instead of the default `serve` (see [Interactive Shell](#interactive-shell) below), or run LibationCli on any host where you can point `--libationFiles` (or `LIBATION_FILES_DIR`) at the folder that you later mount as `/config` on the server.

## Running

Once the configuration files are copied, the docker image can be run with the following command.

```bash
sudo docker run -d \
  -v /opt/libation/config:/config \
  -v /opt/libation/books:/data \
  --name libation \
  --restart=always \
  rmcrackan/libation:latest
```

The container runs `LibationCli serve`: each cycle it scans for new books and downloads them, then sleeps for `SLEEP_TIME` (default `1h`). Pass `--once` (or set `SLEEP_TIME=-1`) to run a single cycle and exit with its result; this is the mode to use from cron or a scheduler.

```bash
sudo docker run --rm \
  -v /opt/libation/config:/config \
  -v /opt/libation/books:/data \
  rmcrackan/libation:latest serve --once
```

`serve` also accepts `--interval`, `--reconcile-on-start` (mark existing books as downloaded before the first cycle), `--listen`, `--metrics-file`, `--accounts-import` and `--readonly-config`; run `LibationCli serve --help` for details.

### Health and metrics

`serve` answers `GET /health` with JSON status on `--listen` (default `http://127.0.0.1:8080`, loopback only, no authentication). The image's `HEALTHCHECK` runs `LibationCli health` against it. To reach it from the host use `--listen http://0.0.0.0:8080` and publish the port.

Set `--metrics-file` (or `LIBATION_METRICS_FILE`) to write a Prometheus textfile after every cycle, suitable for node_exporter's textfile collector.

### HTTP API

Off by default. `--api` (or `LIBATION_API=1`) serves a JSON API under `/api` on the same `--listen` address: `GET /api/status`, `/api/accounts`, `/api/library`, `/api/library/{asin}`, `/api/settings`, `/api/events` (Server-Sent Events), and `POST /api/scan` and `/api/liberate` (run now; `202`, or `409` if a job is running). Account responses never include tokens. `GET /api/openapi.json` is the OpenAPI document for all of it, for generating a client; it follows the same opt-in and token rules.

`GET /api/library` takes `offset` (default 0), `limit` (default 100, at most 1000), `q` and `trash`. `q` uses the same Lucene syntax as the filter box in the app (`author:herbert`, `-tags:skip`, `title:"the hobbit"`); the search index is built or repaired on demand. Results stay newest first, and `total` counts every match, not just the page. A query that cannot be parsed returns `400`. `trash=true` lists the trash instead and cannot be combined with `q`.

```json
{"total":1,"offset":0,"limit":100,"items":[{"asin":"B017V4IM1G","title":"…","authors":["…"],"bookStatus":"NotLiberated","tags":"fav","inTrash":false,"…":"…"}]}
```

Per-book actions return the updated book, `404` for an unknown ASIN and `400` for a body they cannot use:

| Request | Body | Effect |
| --- | --- | --- |
| `PUT /api/library/{asin}/tags` | `{"tags":"one two"}` | Replaces the tags; an empty string clears them. |
| `PUT /api/library/{asin}/status` | `{"status":"liberated"}` or `"notLiberated"` | Sets the liberated status without downloading anything. |
| `POST /api/library/{asin}/trash` | none | Moves the book to the trash. |
| `POST /api/library/{asin}/restore` | none | Restores it. |
| `POST /api/library/{asin}/liberate` | none | Queues the book through the shared queue, re-downloading it if already liberated. `202` with `{"id":"…","asin":"…","title":"…","state":"queued"}`, or `409` if it is already queued. Follow it on `/api/queue` and `/api/events`. |

`GET /api/settings` is read-only: the Books and working folders, file naming templates, download, queue and library options, and whether Audiobookshelf is enabled. It never includes tokens, key files, account data or server URLs. Settings are changed in `Settings.json`.

The download queue is part of the API. `GET /api/queue` returns the running state, the jobs (`working` first, then `queued`), any pending decision and any wait:

```json
{"running":true,"jobs":[{"id":"…","asin":"B017V4IM1G","title":"…","state":"working"}],"pending":[{"id":7,"reason":"badBook","jobId":"…","allowed":["badBook"]}],"waiting":null}
```

The daemon answers its own pauses (a failed book is left for the next cycle, guidance is acknowledged, a full disk waits for space), so `pending` is normally empty. Where one does appear, `POST /api/queue/decisions/{id}` answers it with `{"decision":"continue"}`, `"stop"`, `"resume"` or `{"decision":"badBook","action":"retry","applyToAll":true}` (`action` is `retry`, `ignore` or `abort`). It returns the queue on success, `400` for a body it cannot read, `404` for an unknown or already answered id, and `409` for a decision the pause does not allow.

`/api/events` also carries `BookStarted`, `BookProgress` (at most once a second per book), `BookFinished`, `QueuePaused`, `QueueResumed`, `QueueWaiting` and `QueueWaitEnded`. As with the existing cycle events, the SSE event name is the type name and the data is camelCase JSON.

There are no users. Set `--api-token-file` (or `LIBATION_API_TOKEN_FILE`) to a file containing one token and every `/api` request must send `Authorization: Bearer <token>`; `/health` stays open. `serve` exits if that file is missing or empty. Without a token, anyone who can reach the port controls the daemon, and `serve` logs a warning. Keep it on loopback, or put it behind your reverse proxy with TLS.

### Read-only configuration

When `/config` is a read-only mount (Kubernetes ConfigMaps and Secrets, for example), mount it elsewhere and give Libation a writable state folder:

```bash
sudo docker run -d \
  -v /opt/libation/config:/readonly-config:ro \
  -v /opt/libation/state:/config \
  -e LIBATION_READONLY_CONFIG_DIR=/readonly-config \
  -v /opt/libation/books:/data \
  rmcrackan/libation:latest
```

`Settings.json` is copied from the read-only folder on start and never written back. `AccountsSettings.json` is imported into the state folder whenever its contents change; use `LIBATION_ACCOUNTS_IMPORT` to import it from a different path.

### Interactive Shell

Override the entrypoint to get a shell with the same layout the container uses at runtime, without needing an already-running container to `docker exec` into:

```bash
sudo docker run --rm -it \
  -v /opt/libation/config:/config \
  -v /opt/libation/books:/data \
  --entrypoint bash \
  rmcrackan/libation:latest
```

Or run any one-off command directly, for example `docker run --rm -it -v /opt/libation/config:/config rmcrackan/libation:latest list-accounts`.

## Environment Variables

| Env Var                           | Default | Description                                                                                                                                                              |
| --------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| SLEEP_TIME                        | 1h      | Time between cycles (`30m`, `90s`, `1d`, ...). `-1` runs one cycle and exits, same as `--once`.                                                                          |
| LIBATION_BOOKS_DIR                | /data   | Folder where books will be saved. Overrides `Books` in `Settings.json`.                                                                                                  |
| LIBATION_FILES_DIR                | /config | Libation files folder (`Settings.json`, `AccountsSettings.json`, database). `LIBATION_CONFIG_DIR` is accepted as an alias.                                               |
| LIBATION_READONLY_CONFIG_DIR      |         | Read-only source for `Settings.json` and `AccountsSettings.json`; see above. Must differ from `LIBATION_FILES_DIR`.                                                      |
| LIBATION_ACCOUNTS_IMPORT          |         | Path of an `AccountsSettings.json` to import into the files folder when its contents change.                                                                             |
| LIBATION_API                      |         | `1` or `true` enables the HTTP API (same as `--api`).                                                                                                                    |
| LIBATION_API_TOKEN_FILE           |         | Path of a file holding the bearer token required for `/api` requests.                                                                                                    |
| LIBATION_METRICS_FILE             |         | Path of the Prometheus textfile to write after each cycle.                                                                                                               |
| LIBATION_SQLITE_PATH              |         | Full path of the SQLite database file, e.g. on a separate writable volume. Defaults to `LibationContext.db` in the files folder.                                         |
| LIBATION_CONNECTION_STRING        |         | Connection string for Postgresql. If not present, Libation uses the default sqlite.                                                                                      |
| LIBATION_CONNECTION_STRING_FILE   |         | Path of a file containing the connection string (for Docker/Kubernetes secrets). `LIBATION_CONNECTION_STRING` wins if both are set.                                      |
| LIBATION_MASTER_KEY_FILE          |         | Path to a raw 32-byte master key file (from `export-master-key`). If unset, Libation also looks for `libation-master.key` under the Libation files directory.            |
| LIBATION_MASTER_KEY               |         | Base64-encoded 32-byte master key (alternative to a key file).                                                                                                           |

## User

This docker image runs as user `1001`. In order for the image to function properly, user `1001` must be able to read and write the volumes that are mounted in. If they are not, you will see errors, including [sqlite error](https://github.com/rmcrackan/Libation/issues/1060), [Microsoft.Data.Sqlite.SqliteException](https://github.com/rmcrackan/Libation/issues/1110), [unable to open database file](https://github.com/rmcrackan/Libation/issues/1113), [Microsoft.EntityFrameworkCore.DbUpdateException](https://github.com/rmcrackan/Libation/issues/1049)

If you're not sure what your user number is, check the output of the `id` command. Docker should normally run with the number of the user who configured and ran it.

If you want to change the user the image runs as, you can specify `-u <uid>:<gid>`. For example, to run it as user `2000` and group `3000`, you could do the following:

```bash
sudo docker run -d \
  -u 2000:3000 \
  -v /opt/libation/config:/config \
  -v /opt/libation/books:/data \
  --name libation \
  --restart=always \
  rmcrackan/libation:latest
```

If the user it's running as is correct, and it still cannot write, be sure to check whether the files and/or folders might be owned by the wrong user. You can use the `chown` command to change the owner of the file to the correct user and group number, for example: `chown -R 1001:1001 /mnt/audiobooks /mnt/libation-config`

## Upgrading from the shell-script image

Earlier images ran `liberate.sh`. That script, `jq`, and the `/config-internal` staging folder are gone; the image runs `LibationCli serve` against `/config` directly.

- `SLEEP_TIME` now defaults to `1h` (it was `-1`). Set `SLEEP_TIME=-1` or pass `serve --once` to keep the run-once behavior.
- `Books` comes from `LIBATION_BOOKS_DIR` only when that variable is set (the image sets it to `/data`). `InProgress` is no longer forced to `/tmp`; it follows `Settings.json`.
- Mounting `/config` read-only: mount it at another path, set `LIBATION_READONLY_CONFIG_DIR` to that path, and mount a writable volume at `/config`.
- `LIBATION_DB_DIR` and `LIBATION_DB_FILE` are replaced by `LIBATION_SQLITE_PATH` (the full path of the database file). The database is no longer discovered by scanning for `*.db` files.
- `LIBATION_CREATE_DB` and `LOG_LEVEL` no longer exist; the database is created when missing.
- `LIBATION_CONFIG_DIR` still works as an alias for `LIBATION_FILES_DIR`.
- Custom commands: arguments to `docker run` are now `LibationCli` arguments, not a command to run in place of the script. Use `--entrypoint` for a shell.

## Troubleshooting

- **`Failed to decrypt ExistingAccessToken` (scan finds no books after copying Windows config):** See the [encrypted-tokens warning](#configuration) at the top of Configuration, plus [Troubleshooting](/docs/advanced/troubleshoot#failed-to-decrypt-existingaccesstoken-docker-finds-no-new-books) and the [FAQ](/docs/frequently-asked-questions#docker-finds-no-new-books-failed-to-decrypt-existingaccesstoken).
- **`Failed to encrypt identity field ... Saving as plaintext`:** Harmless for operation - encryption could not run (usually no master key / OS store in the container), so tokens were saved as plaintext and the run continued. Supply a master key if you want encryption at rest. See the [encrypted-tokens warning](#configuration).
- **Library scan appears to hang in Docker:** Try setting `ImportEpisodes` to `false` in `Settings.json` on your config volume and run again. That turns off the extra episode/podcast catalog requests during import and helps narrow down whether the stall is in that path versus auth or the network.

## Advanced Database Options

Set `LIBATION_SQLITE_PATH` to put the SQLite database on a different volume than the rest of the configuration, so the configuration can be mounted read-only (see [Read-only configuration](#read-only-configuration)). If the file does not exist Libation creates it.

## Logging

LibationCli already writes a `LogYYYYMM.log` file (rolling monthly) using the same logging setup as the desktop apps — no extra configuration is required to generate it. In the docker image the log is written to the Libation files folder (`/config`), so it persists with your config volume. To keep it elsewhere:

- **Point the log file at an already-mounted directory** by adding a `Serilog` section to your `Settings.json` (in your `/config` volume) with a `File` sink `path` pointing somewhere persisted, such as `/data/Log.log`. Libation uses an existing `Serilog` section as-is apart from filling in the size-rolling arguments described below when they are missing:

  ```json
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "/data/Log.log",
          "rollingInterval": "Month",
          "fileSizeLimitBytes": 10485760,
          "rollOnFileSizeLimit": true,
          "retainedFileCountLimit": 20,
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] (at {Caller}) {Message:lj}{NewLine}{Exception} {Properties:j}",
          "hooks": "LibationFileManager.FileSinkHook, LibationFileManager, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
        }
      }
    ],
    "Using": [
      "Dinah.Core",
      "Serilog.Exceptions"
    ],
    "Enrich": [
      "WithCaller",
      "WithExceptionDetails"
    ]
  }
  ```

### Log size

The log rolls on **both** the calendar month and file size: a new file is started every 10 MB
(`Log202608.log`, `Log202608_001.log`, ...) and the 20 newest files are kept, so the logs stay
around 200 MB at most and any single file is small enough to attach to a bug report.

A frequent cron schedule and many accounts is what makes this matter: every run logs a startup
block and every account's library scan logs a line per page of results, so several runs an hour
across a dozen-plus accounts produces several MB a day. `rollingInterval` alone does not bound
that — before this was the default, a single month's log could reach tens of MB, and Serilog's own
1 GB ceiling would eventually stop it logging altogether until the month rolled over.

To change any of it, set `fileSizeLimitBytes`, `rollOnFileSizeLimit` or `retainedFileCountLimit`
yourself in `Settings.json`; Libation only fills in the ones you leave out. Lowering
`MinimumLevel` is not an option (`Information` is the lowest that still records what a bug report
needs), but a smaller `retainedFileCountLimit` bounds total disk use.

## Getting Help

As mentioned above: docker is not officially supported. I'm adding this at the bottom of the page for anyone serious enough to have read this far. If you've tried everything above and would still like help, you can open an [issue](https://github.com/rmcrackan/Libation/issues). Please include `[docker]` in the title. There are also some docker folks who have offered occasional assistance who you can tag within your issue: `@ducamagnifico` , `@wtanksleyjr` , `@CLHatch` , `@oxivanisher`.

**Reminder** that these are just friendly users who are sometimes around. They're _not_ our customer support.
