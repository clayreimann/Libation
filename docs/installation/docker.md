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

`serve` also accepts `--interval`, `--reconcile-on-start` (mark books whose file is found as downloaded before the first cycle; see [Books outside the Books folder](#books-outside-the-books-folder)), `--listen`, `--metrics-file`, `--accounts-import` and `--readonly-config`; run `LibationCli serve --help` for details.

### Books outside the Books folder

Like the desktop app, the daemon trusts each book's stored status: a book marked downloaded is never downloaded again because its file is missing, and only an explicit action (`PUT .../status`, `set-status`, `liberate`) moves it back to pending. A book that is still pending is downloaded unless Libation can find its file, and it looks in two places: the Books folder (matching the ASIN in the file name) and the file locations recorded in `FileLocationsV2.json` in the Libation files folder.

`--reconcile-on-start` runs a scan and then `set-status --downloaded`. It only ever promotes: a pending book whose file is found becomes downloaded, and nothing is demoted. A file that is neither in the Books folder nor recorded is not found, so that book is downloaded again, exactly as on the desktop until you use Locate file or Locate audiobooks. Copying `FileLocationsV2.json` from a desktop install carries those locations over.

The HTTP API has the same two tools. Both only accept paths under the Books folder (mount or symlink other folders beneath it), reject `..`, and for a file only audio files:

- `POST /api/library/{asin}/locate` with `{"path":"/data/Author/Book/book.m4b"}` records the file for the book, as Locate file does, and marks it downloaded. `200` with the book detail, `400` for a path outside Books, with `..`, missing, or not audio, `404` for an unknown ASIN.
- `POST /api/library/locate-scan` with `{"folder":"/data/Imported"}` reads the tags of every `.mp3` and `.m4b` in the folder and records and marks each book whose ASIN is embedded, as Locate audiobooks does. It runs in the background: `202` with `{"state":"running","folder":"…","found":0,"matched":0,"updated":0,…}`, `400` for a bad folder, `409` if a scan is already running. `/api/events` carries `LocateScanProgress` with the same fields under `scan` as each file is found and when it ends (`state` is `completed` or `failed`), and `GET /api/library/locate-scan` returns the latest state.

### Health and metrics

`serve` answers `GET /health` with JSON status on `--listen` (default `http://127.0.0.1:8080`, loopback only, no authentication). The image's `HEALTHCHECK` runs `LibationCli health` against it. To reach it from the host use `--listen http://0.0.0.0:8080` and publish the port.

Set `--metrics-file` (or `LIBATION_METRICS_FILE`) to write a Prometheus textfile after every cycle, suitable for node_exporter's textfile collector.

### HTTP API

Off by default. `--api` (or `LIBATION_API=1`) serves a JSON API under `/api` on the same `--listen` address: `GET /api/status`, `/api/accounts`, `/api/library`, `/api/library/asins`, `/api/library/stats`, `/api/library/{asin}`, `/api/library/{asin}/cover`, `/api/library/{asin}/audio/files`, `/api/library/{asin}/audio`, `/api/library/{asin}/chapters`, `/api/library/{asin}/locate`, `/api/library/locate-scan`, `/api/settings` (also `PATCH`), `POST /api/settings/templates/preview`, `POST /api/audiobookshelf/test`, `GET /api/audiobookshelf/libraries`, `POST /api/audiobookshelf/upload`, `/api/events` (Server-Sent Events), and `POST /api/scan` and `/api/liberate` (run now; `202`, or `409` if a job is running). Account responses never include tokens. `GET /api/openapi.json` is the OpenAPI document for all of it, for generating a client; it follows the same opt-in and token rules.

`GET /api/accounts` lists each account as `{"accountId":"a@example.com","accountName":"Main","locale":"us","libraryScan":true,"authState":"signedIn","credentials":"modern","additionalLocales":[]}`. `authState` is `needsLogin` when the account has no usable login: never signed in, no refresh token, or marked for re-registration, as the app's Accounts dialog shows it; an expired access token with a refresh token is still `signedIn`. `credentials` is `modern`, `legacy` (works, but signing in again upgrades it) or `none`. `additionalLocales` are the extra marketplaces the account also scans. `GET /api/status` has `accountsManagedByImport` (true when accounts come from `LIBATION_ACCOUNTS_IMPORT`) and `accountsNeedingLogin`, the ids of accounts that are `needsLogin` or whose last scan or download failed on auth, until the next successful scan. `/api/events` carries `AccountNeedsLogin` (`{"accountId":"a@example.com","at":"…"}`) the first time a scan or download fails on auth, where the desktop app would ask you to log in, and `AccountSignedIn` (`{"accountId":"a@example.com","locale":"us","at":"…"}`) after a login over the API and when a successful scan clears an account's needs-login flag.

| Request | Body | Effect |
| --- | --- | --- |
| `PATCH /api/accounts/{id}` | `{"accountName":"New name","libraryScan":false}`, either field | Renames the account and/or sets whether it is scanned. The marketplace cannot change; add the account again for another one. Returns the account. |
| `DELETE /api/accounts/{id}` | none | Removes the account, as the Accounts dialog does. Its books stay in the library. Returns the removed account. |
| `POST /api/scan` | optional `{"accounts":["a@example.com","Main"],"locale":"uk"}` | Scans only those accounts (login id or name, as `libationcli scan` takes them); no body scans all. `locale` limits the scan to one marketplace, for a login registered in several, and on its own scans every account in it. `400` for an empty list, a blank locale, or an unknown account or marketplace. |

`{id}` is the login id. If one login is registered in two marketplaces, add `?locale=uk` or the call is `409`. `404` for an unknown account, `400` for an empty body or blank name. Account changes return `409` when the accounts file cannot be written, and also when accounts come from an import file (`--accounts-import`, `LIBATION_ACCOUNTS_IMPORT` or `--readonly-config`): that file is the source of truth and a changed copy replaces the accounts in the state folder wholesale, so an edit made over the API would silently be lost. Change the imported file instead.

**Signing in over the API.** An account that needs a login (`authState: needsLogin`, or an `AccountNeedsLogin` event) can be signed in again, and a new one added, without the desktop app. It needs `--api-token-file`: without a token every `/api/accounts/login` route is `403`. Never put the URLs below in shared logs or chat; the final one works like a password.

1. `POST /api/accounts/login` with `{"accountId":"a@example.com","locale":"us","deviceRegistration":"CurrentAndroid"}` (`deviceRegistration` optional, new sign-ins only; `accountId` may be left out when exactly one account uses that locale) returns `{"loginId":"…","url":"https://www.amazon.com/ap/signin?…","expiresAt":"…"}`.
2. Open `url` in a browser and sign in. The page you land on may say it does not exist; copy the full address from the address bar.
3. `POST /api/accounts/login/{loginId}` with `{"responseUrl":"<that address>"}` saves the tokens (encrypted with the master key) and returns the account. `400` when it is not the final URL (`"code":"notFinalUrl","retryable":true`, the login stays usable) or Audible rejected it (`"code":"rejected","retryable":false`, start again); `404` when the login was already used, cancelled or has expired.

`GET /api/accounts/login` lists the open sign-ins as `[{"loginId":"…","accountId":"…","locale":"us","url":"…","expiresAt":"…"}]`, so a reloaded page can offer them again; it returns the sign-in address only, never a response URL. `DELETE /api/accounts/login/{loginId}` cancels. A pending login lasts 10 minutes, is kept in memory only (a restart forgets it), is single use, and at most 5 can be pending (`429`). Under an import file a new account is `409` and signing in again for an account in the file works: the new tokens are written to the live accounts file and stay there, because the file is re-imported only when its contents (sha256) change. See [ADR 0001](../adr/0001-api-browser-login.md).


`GET /api/library` takes `offset` (default 0), `limit` (default 100, at most 1000), `q`, `trash`, `sort` and `dir`. `q` uses the same Lucene syntax as the filter box in the app (`author:herbert`, `-tags:skip`, `title:"the hobbit"`); the search index is built or repaired on demand. `trash=true` lists the trash instead, and `q` filters it too. Results stay newest first unless `sort` is one of `title`, `author`, `narrator`, `series`, `length`, `purchaseDate`, `added`, `status`, `rating` or `lastDownload`, with `dir=asc` (default) or `desc`; missing values sort last and ties break by title. `total` counts every match, not just the page. A query that cannot be parsed, or a bad `sort`, `dir` or `size`, returns `400`.

**Audio for a player.** `GET /api/library/{asin}/audio/files` lists the liberated book's audio as `[{"index":0,"name":"Book.m4b","contentType":"audio/mp4","sizeBytes":123,"durationSec":3600.5}]`: one entry for a single m4b or mp3, one per part, in play order, for a split book. `durationSec` is `null` when the file cannot be read. `GET /api/library/{asin}/audio?file=0` streams that entry (`file` defaults to 0) as `audio/mp4` (m4b, m4a) or `audio/mpeg` (mp3) with `Accept-Ranges: bytes`, `ETag`, `Last-Modified` and `Cache-Control: private`. A `Range` request gets `206` with `Content-Range`, a full request `200`, a matching `If-None-Match` `304`, an unsatisfiable range `416`. The file is found on the server, never from the request. `GET /api/library/{asin}/chapters` lists `[{"index":0,"title":"Opening","startSec":0,"durationSec":60,"fileIndex":0,"fileStartSec":0}]` from the chapters embedded in the file, else the `.cue` sheet (the mp3's embedded `CUESHEET` or the `.cue` file beside a single-file book), else one chapter per file titled by its file name. For a split book `startSec` counts from the start of the book and `fileStartSec` from the start of file `fileIndex`. All three are `404` with `{"error":"…"}` for an unknown book, a book that is not liberated, or missing files, and the audio route is also `404` for a `file` out of range. The bearer token applies as everywhere under `/api`. A browser `<audio>` tag cannot send an `Authorization` header, so in the web image the nginx proxy adds the token to `/api` requests and the page points `<audio src>` at `/api/library/{asin}/audio`.

Each book carries categories, product and personal ratings, `hasPdf`, `isAudiblePlus`, `includedUntil`, last-download time, version and format, `isSpatial`, `pictureId`, `parentAsin` (podcast episodes) and `absentFromLastScan`. `partialDownload` (a half-downloaded file in the in-progress folder) is on every book, from one cached folder listing per page. `GET /api/library/{asin}` adds the description, publisher, language, copyright, category ladders and `audioFileExists`, a per-book file check that list pages skip. Fields that are not nullable are `required` in the OpenAPI document, and `bookStatus`, `pdfStatus`, queue `state`, and `result` and `status` on finished jobs and `BookFinished` are string enums there with the same values as on the wire.

Podcast parent rows are not in `/api/library`, as in the app's index. Each episode has `parentAsin`, and `GET /api/library/podcasts?q=&trash=` returns the parents (`isPodcastParent: true`) of the episodes the same `q` and `trash` match, ordered by title. A parent's `series` holds only its own series links, as the app's Series column does, so it is often empty.

`GET /api/library/export?format=csv|xlsx|json&q=&trash=` downloads the same file as the app's Export and `libationcli export`, newest first, filtered like `/api/library`, as `libation-library-YYYYMMDD.<ext>` (`libation-trash-…` with `trash=true`). A bad `format` or `q` returns `400`.

`GET /api/quick-filters` returns `{"useDefault":false,"filters":[{"filter":"tag:skip","name":"Skipped"}]}` in display order, the app's quick filter list and its use-first-filter-by-default flag. `PUT /api/quick-filters` with the same shape replaces the whole list. Each filter must parse as a library query: `400` names the first bad one as `filters[1]: …`. Blank and duplicate filters are dropped. The list lives in `QuickFilters.json` in the Libation files folder, so it stays writable in read-only-config mode; `409` only if that file cannot be written.

`GET /api/library/asins?q=&trash=` returns `{"total":N,"asins":[…]}` for select-all, with `asins` capped at 50000. `GET /api/library/stats` returns the status-bar counts (`total`, `liberated`, `partial`, `notLiberated`, `error`, `unavailable`, and `pdfLiberated`, `pdfNotLiberated`, `pdfUnavailable`) from the database.

`GET /api/library/{asin}/cover?size=small|large` returns the jpeg (80px or 500px) from the picture cache, fetching it from Audible on first use, with an `ETag` and a one-year immutable `Cache-Control`; a matching `If-None-Match` gets `304`. An unknown ASIN, a book with no cover, or a failed fetch returns `404`.

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

Bulk actions take `{"asins":[…]}` (at most 5000, duplicates are collapsed) and never fail the whole request over one book. They return `200` (`202` for liberate) with `updated` (the count of `ok`), `missing` (the unknown ASINs) and a `result` per ASIN, in request order: `ok`, `notFound` or `conflict`. An empty, missing or oversized list, or a bad `status`, returns `400`.

| Request | Body | Effect |
| --- | --- | --- |
| `PUT /api/library/tags` | `{"asins":[…],"tags":"one two"}` or `{"asins":[…],"add":"x","remove":"y"}` | Replaces the tags, or adds and removes individual tags. `tags` cannot be combined with `add` or `remove`. |
| `PUT /api/library/status` | `{"asins":[…],"status":"liberated"}` | Sets the liberated status. |
| `PUT /api/library/pdf-status` | `{"asins":[…],"status":"notLiberated"}` | Sets the PDF status; books without a PDF are `conflict`. |
| `POST /api/library/trash` | `{"asins":[…]}` | Moves books to the trash; books already there are `ok`. |
| `POST /api/library/restore` | `{"asins":[…]}` | Restores books; books not in the trash are `ok`. |
| `POST /api/library/purge` | `{"asins":[…]}` | Permanently deletes books that are in the trash, which cannot be undone; any other book is `conflict`. |
| `POST /api/library/liberate` | `{"asins":[…]}` | Queues each book; one already queued is `conflict`. |

```json
{"updated":1,"missing":["nope"],"results":[{"asin":"B017V4IM1G","result":"ok"},{"asin":"B000000002","result":"conflict"},{"asin":"nope","result":"notFound"}]}
```

`GET /api/settings` returns the Books and working folders (with whether each exists and is writable), file naming templates, download, MP3, queue and library options, log level, replacement characters, and the Audiobookshelf options (including `audiobookshelf.tokenSet`, whether an API token is saved). It never includes tokens, key files or account data.

`PATCH /api/settings` changes settings. Send only what should change, in the shape `GET` returns: `{"books":"/data/Books","naming":{"fileTemplate":"<title> [<id>]"},"download":{"createCueSheet":true},"mp3":{"bitrate":96},"queue":{…},"library":{…},"logLevel":"Debug","replacementCharacters":[…]}`; `inProgress: null` resets the working folder. Everything is validated before anything is written, as the app's Settings dialog does (templates through the real template engine, folders absolute and writable, ranges and enums): `200` returns the updated settings, with `restartRequired` listing any changed keys that need a restart; `400` returns `{"error":"…","fields":{"naming.fileTemplate":"…"}}` and changes nothing; `409` when the configuration is read-only (see below) or cannot be saved. `books` is also `400` while `LIBATION_BOOKS_DIR` is set. Changes apply to the next book, not to jobs already running. GUI-only settings, token storage, and the download-speed and concurrency controls are not available here.

The Audiobookshelf options are the `audiobookshelf` section of the same `GET` and `PATCH`: `{"audiobookshelf":{"enabled":true,"includePdfs":false,"serverUrl":"https://abs.example.com","apiToken":"…","libraryId":"…","folderId":"…"}}`. `enabled` turns on the automatic upload after each liberation. `serverUrl` must be an absolute `http` or `https` address without a user name or password. `apiToken` is write-only: `GET` reports only `tokenSet`, and a `null` token, URL, library or folder clears it. Naming a different `libraryId` without a `folderId` clears the folder, and a `folderId` needs a library. The token is stored the way the desktop app stores it (encrypted when token storage is encrypted).

`POST /api/audiobookshelf/test` checks a connection: send `{"url":"…","token":"…"}` to try unsaved values, or nothing (or either field) to use the saved ones; a `url` other than the saved one needs a `token`. It always answers `200` with `{"ok":true,"serverVersion":"…","user":"…"}` or `{"ok":false,"error":"…","code":"badUrl|unreachable|unauthorized|serverError|notConfigured"}`, never echoing the token. `GET /api/audiobookshelf/libraries` lists the book libraries and their folders, `[{"id","name","mediaType","folders":[{"id","path"}]}]`, to pick `libraryId` and `folderId`; `409` when no URL and token are saved, `502` with a `code` when the server cannot be reached or rejects the token. `POST /api/audiobookshelf/upload` with `{"asins":["B0…"]}` queues an upload-only job per book, which shows up in `/api/queue`, history, logs and events and can be cancelled or moved like any job: `202` with a result per ASIN (`conflict` for a book that is not liberated or already has a queued job), `409` unless Audiobookshelf is enabled with a URL, token, library and folder. Upload problems are logged on the job but never fail it, as after a liberation. Syncing Audible listening progress is not available here.

`POST /api/settings/templates/preview` with `{"template":"<title>","kind":"file","asin":"B0…"}` (`kind` is `folder`, `file`, `chapterFile` or `chapterTitle`; `asin` optional, a sample book is used without it) returns `{"valid":true,"errors":[],"warnings":[],"preview":"…"}`, with `preview` null when the template is invalid; `404` for an unknown `asin`.

The download queue is part of the API. `GET /api/queue` returns the running state, the jobs (`working` first, then `queued`), any pending decision and any wait:

```json
{"running":true,"jobs":[{"id":"…","asin":"B017V4IM1G","title":"…","state":"working"}],"pending":[{"id":7,"reason":"badBook","jobId":"…","allowed":["badBook"]}],"waiting":null,"paused":false,"stats":{"queued":3,"completed":12,"errors":1,"runningSince":"2026-08-10T12:00:00+00:00"}}
```

`paused` is your own hold on starting new books (below), separate from `pending` and `waiting`. `stats` are the counters beside the desktop queue: `queued` books waiting, `completed` successes and `errors` (failed, validation fail, widevine, disk full) among the finished ones, and when the current run began (`runningSince` is `null` when none is in progress). `completed` and `errors` reset with `DELETE /api/queue/history`.

The daemon answers its own pauses (a failed book is left for the next cycle, guidance is acknowledged, a full disk waits for space), so `pending` is normally empty. Where one does appear, `POST /api/queue/decisions/{id}` answers it with `{"decision":"continue"}`, `"stop"`, `"resume"` or `{"decision":"badBook","action":"retry","applyToAll":true}` (`action` is `retry`, `ignore` or `abort`). It returns the queue on success, `400` for a body it cannot read, `404` for an unknown or already answered id, and `409` for a decision the pause does not allow.

The queue controls match the desktop's:

| Request | Body | What it does |
| --- | --- | --- |
| `DELETE /api/queue/jobs/{id}` | none | Removes a queued job (`{"id":"…","outcome":"removed"}`), or cancels a running one (`"outcome":"cancelling"`), which then finishes as `cancelled`. `404` for an unknown or finished job. |
| `POST /api/queue/cancel` | none | Clears the queue and cancels every running job, like Cancel All. Returns the queue. |
| `POST /api/queue/jobs/{id}/move` | `{"to":"top"}` (`top`, `up`, `down`, `bottom`) | Reorders a queued job. Returns the queue. `400` bad body, `404` unknown job, `409` job not queued. |
| `POST /api/queue/pause` | none | Stops starting new jobs while running ones finish. Held until resumed, including across runs. Returns the queue. |
| `POST /api/queue/resume` | none | Starts jobs again. Both are fine to repeat. |
| `GET /api/queue/history` | none | The last 200 finished jobs, newest first: `[{"id":"…","asin":"…","title":"…","result":"success","status":"completed","finishedAt":"…"}]`. |
| `DELETE /api/queue/history` | none | Forgets finished jobs, their logs and the completed and error counts, like Clear Finished. Returns the queue. |
| `GET /api/queue/jobs/{id}/log` | none | The job's last 500 log lines, oldest first: `{"jobId":"…","dropped":0,"lines":[{"at":"…","level":"info","message":"…"}]}`. `level` is `info`, `warning` or `error`. Works for queued, running and finished jobs; `404` once forgotten. |

Log lines are the ones the desktop shows per book. URL query strings and credentials, and values after `token`, `password`, `secret` or `Bearer`, are removed before a line is stored or sent, and each line is cut at 2000 characters.

`/api/events` also carries `QueueDispatchPaused` and `QueueDispatchResumed` (no payload) when you pause or resume, and `BookLog` (`{"jobId":"…","level":"info","message":"…","at":"…"}`, `at` being when the line was written). Info lines are limited to 20 a second per book on the stream; warnings and errors are never held back, and `/log` has every line. It also carries `BookStarted`, `BookProgress` (at most once a second per book), `BookFinished`, `QueuePaused`, `QueueResumed`, `QueueWaiting` and `QueueWaitEnded`. `QueueChanged` (`{"queue":{…}}`, the same body as `GET /api/queue`) follows every change to the queue from any source, whether the API, the scheduled cycle or a bulk liberate: queued, removed, moved, started or finished. Bursts are coalesced into one event per 100 ms. Every event has a schema in the OpenAPI document, and `EngineEvent` is a `oneOf` of them. As with the existing cycle events, the SSE event name is the type name and the data is camelCase JSON.

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

`Settings.json` is copied from the read-only folder on start and never written back, so `PATCH /api/settings` is `409`. `AccountsSettings.json` is imported into the state folder whenever its contents change; use `LIBATION_ACCOUNTS_IMPORT` to import it from a different path.

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
