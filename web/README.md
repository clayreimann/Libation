# Libation web UI

Browser UI for the libationd daemon (`LibationCli serve --api`): library search, book details drawer, counts bar, quick filters (kept in localStorage), bulk actions on a selection (liberate, status, PDF status, tags, trash, restore, permanent delete), covers, sortable and configurable columns, locate file and locate audiobooks (server paths), queue with pause, reorder, remove/cancel, per-job logs and history, accounts (rename, scan on/off, remove, scan one or several, browser sign-in, needs-login warning; sign-in needs the daemon's `--api-token-file`), status, and editable settings (naming templates with live preview, Audiobookshelf connection test, pickers and upload), and a persistent audio player for liberated books (chapters, skip, speed, resume position), convert to MP3 (one book or a selection, through the queue), series browsing in book order, editable star ratings pushed to Audible, a Find better quality scan with apply and download, and clips and bookmarks export. React, Vite and TypeScript; talks to `/api` only.

## Develop

```sh
cd web
npm ci
LIBATION_API_URL=http://localhost:8080 npm run dev   # proxies /api, SSE included
npm run build                                        # tsc --noEmit, then vite build
npm test
npm run gen:api                                      # types from openapi.json
```

`LIBATION_API_TOKEN` is added to proxied requests in dev when set. Refresh `openapi.json` from `GET /api/openapi.json` when the API changes, then `npm run gen:api`.

## Image

`ghcr.io/clayreimann/libation-web` serves the UI and proxies `/api/` to the daemon on port 8080 (non-root).

| Variable | Default | |
| --- | --- | --- |
| `LIBATION_API_URL` | `http://libation:8080` | Daemon base URL, no path. |
| `LIBATION_API_TOKEN` | | Sent as `Authorization: Bearer ...` when set. |
| `LIBATION_API_TOKEN_FILE` | | File holding the token, used if the variable is empty. |

`/healthz` returns 200.

```yaml
services:
  libation:
    image: ghcr.io/clayreimann/libation:libationd
    command: ["serve", "--api", "--listen", "http://0.0.0.0:8080"]
    environment:
      LIBATION_API_TOKEN_FILE: /run/secrets/libation_api_token
    secrets: [libation_api_token]
    # no ports: only the UI container reaches it

  web:
    image: ghcr.io/clayreimann/libation-web:latest
    ports: ["8080:8080"]
    environment:
      LIBATION_API_URL: http://libation:8080
      LIBATION_API_TOKEN_FILE: /run/secrets/libation_api_token
    secrets: [libation_api_token]
    depends_on: [libation]

secrets:
  libation_api_token:
    file: ./api-token
```

Daemon volumes and options are in `docs/installation/docker.md`; it must listen on a non-loopback address for the UI container to reach it.

The UI has no authentication of its own, and the proxy adds the API token for every visitor. Put it behind your reverse proxy's auth.
