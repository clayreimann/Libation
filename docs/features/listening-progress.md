# Listening Progress

Libation can read where you are in each audiobook (the position Audible syncs between your devices) and either save it as JSON or copy it to [Audiobookshelf](/docs/features/audiobookshelf). It is available from the CLI.

Audible does not document this API. Libation uses the endpoint the Audible apps use to sync your position, so it may change without notice.

## Save progress as JSON

```console
libationcli progress export -p progress.json
```

Without `-p` the JSON is printed to stdout. Limit the run to some titles or accounts:

```console
libationcli progress export B017V4IM1G B017V4NOZ0
libationcli progress export --account my-nickname
```

Each item has the ASIN, title, authors, account, marketplace, `positionMs`, `lastUpdated` (when Audible last received a position, in ISO 8601), `lengthMs`, and `isFinished`. Titles Audible has no position for are left out. `lengthMs` and `isFinished` come from Libation's last library scan, so scan first for current values.

## Copy progress to Audiobookshelf

Uses the server URL, API token, and library from **Settings -> Audiobookshelf**. The automatic upload checkbox does not need to be on.

```console
libationcli progress push-abs --dry-run
libationcli progress push-abs
```

Progress is written for the Audiobookshelf user that owns the API token.

- **Matching:** Libation looks for the book's ASIN in Audiobookshelf's metadata first. If no item has it, it falls back to title (with or without subtitle) and author. If more than one item matches the title, the book is skipped rather than guessed.
- **Overwriting:** By default Libation leaves Audiobookshelf alone when its progress was updated more recently than Audible's, when the position is already within 5 seconds, or when Audiobookshelf marks the book finished. Use `--force` to overwrite anyway.
- **Finished:** A book Libation's library has as finished is marked finished on Audiobookshelf. Libation never clears a finished flag.
- Use `--library-id` to match against a different library than the one in Settings.

The run ends with a count of each outcome. It exits with an error code if any update failed.

## Which device the progress came from

Audible keeps one synced position per title: whichever device or app sent it last. The progress response does not say which device that was, so Libation cannot filter progress by device yet.

To help find out whether Audible exposes this anywhere, `progress probe` saves the raw response of every Audible endpoint known to carry position or listening data for one title:

```console
libationcli progress probe B017V4IM1G -o ./probe
```

It lists any field whose name suggests a device, client, or session. To test, listen to a title on one device, wait a minute, and run the probe. Then do the same on another device and compare the files. The files can contain account identifiers, so review them before sharing.
