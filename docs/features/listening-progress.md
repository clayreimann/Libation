# Listening Progress

Libation can read where you are in each audiobook (the position Audible syncs between your devices) and either save it as JSON or copy it to [Audiobookshelf](/docs/features/audiobookshelf). It is available from the CLI.

Audible does not document this API. Libation uses the endpoint the Audible apps use to sync your position (`/1.0/annotations/lastpositions`), so it may change without notice. It was last checked against a real Audible account in November 2026.

## Save progress as JSON

```console
libationcli progress export -p progress.json
```

Without `-p` the JSON is printed to stdout. Limit the run to some titles or accounts:

```console
libationcli progress export B017V4IM1G B017V4NOZ0
libationcli progress export --account my-nickname
```

Each item has the ASIN, title, authors, account, marketplace, `positionMs`, `lastUpdated` (when Audible last received a position, ISO 8601 in UTC), `lengthMs`, and `isFinished`. Titles Audible has no position for are left out.

- `lengthMs` and `isFinished` come from Libation's last library scan, so scan first for current values. Libation stores length in whole minutes, so a finished title's `positionMs` can be up to a minute past `lengthMs`.
- Each account is read separately. If one fails, for example because the titles belong to an account that has since been removed from Libation, the error is printed, the other accounts are still exported, and the command exits with an error code.
- Podcast series have no position of their own. Positions for individual podcast episodes have not been tested.

## Copy progress to Audiobookshelf

Uses the server URL, API token, and library from **Settings -> Audiobookshelf**. The automatic upload checkbox does not need to be on.

```console
libationcli progress push-abs --dry-run
libationcli progress push-abs
```

Progress is written for the Audiobookshelf user that owns the API token.

> This command has so far been tested only against a stub server, not a real Audiobookshelf install.

- **Matching:** Libation looks for the book's ASIN in Audiobookshelf's metadata first. If no item has it, it falls back to title (with or without subtitle) and author. If more than one item matches the title, the book is skipped rather than guessed.
- **Overwriting:** By default Libation leaves Audiobookshelf alone when its progress was updated more recently than Audible's, when the position is already within 5 seconds, or when Audiobookshelf marks the book finished. Use `--force` to overwrite anyway.
- **Finished:** A book Libation's library has as finished is marked finished on Audiobookshelf. Libation never clears a finished flag.
- Use `--library-id` to match against a different library than the one in Settings.

The run ends with a count of each outcome. It exits with an error code if any update failed.

## Which device the progress came from

Audible does not say which device a position came from, so Libation cannot filter progress by device.

Audible keeps one synced position per title, from whichever device or app sent it last. In November 2026 a book was played in the Audible web player and then in the Audible phone app, and every endpoint below was read after each. Between the two, only the position, its timestamps and the listening-time totals changed. None of the responses had a device, client, serial or session field.

| Endpoint | What it returns |
|---|---|
| `GET /1.0/annotations/lastpositions?asins=` | `position_ms`, `last_updated`, `status` per ASIN. This is what Libation uses. |
| `GET /1.0/content/{asin}/metadata?response_groups=last_position_heard` | The same position and timestamp. |
| `GET /1.0/library/{asin}?response_groups=listening_status` | `percent_complete`, `time_remaining_seconds`, `is_finished` |
| `GET /1.0/stats/status/finished?asin=` | Finished flag and when it was last set |
| `GET /1.0/stats/aggregates` | Listening time totals per day or month, for the whole account |
| Kindle sidecar (`cde-ta-g7g.amazon.com/FionaCDEServiceEngine/sidecar?type=AUDI`) | A single `audible.last_heard` record: position and creation time |

`GET /1.0/lastpositions` returns 403 to Libation's device credentials, and `/1.0/devices` does not exist (404). Asking lastpositions for extra response groups such as `history` or `device` is rejected. The Audible apps do send their device and app version when they report listening events, but no endpoint Libation can read returns that information.

To check again if Audible changes this, `progress probe` saves the raw response of each endpoint above for one title:

```console
libationcli progress probe B017V4IM1G --output ./probe
```

It lists any field whose name suggests a device, client or session. Listen to a title on one device, wait a minute and run the probe, then do the same on another device and compare the files. The files contain account identifiers, so review them before sharing.
