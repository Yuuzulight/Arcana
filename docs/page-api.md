# Arcana page API v1

The page API is the engine-neutral boundary for Mana. The current .NET API is the reference implementation; the loopback transport described here is the wire form that the direct Mana client will use.

## Version and transport

- The current contract version is `v1`.
- HTTP binds to `127.0.0.1` only. A server may choose an ephemeral port and must report the selected port to its host.
- Every request carries `Authorization: Bearer <token>`.
- Requests with a non-empty `Origin` are accepted only when the origin is loopback (`http://127.0.0.1`, `http://localhost`, or an explicitly configured loopback port). Foreign origins receive `403` before a page operation runs.
- JSON uses UTF-8. Unknown request fields are ignored; unknown operation names and malformed required fields return `400`.
- `GET /v1/health` returns `{ "apiVersion": "v1", "engine": { ... } }` and does not create a browser page.
- WebSocket events use `GET /v1/events`; each message is `{ "type": "event", "pageId": "...", "name": "...", "data": { ... } }`.

## Page lifecycle

- `GET /v1/tabs` lists live page targets with stable `targetId`, URL, and title fields.
- `POST /v1/pages` with `{ "url": "https://..." }` creates a page and returns `{ "pageId": "...", "url": "..." }`.
- `DELETE /v1/pages/{pageId}` closes the page.
- `POST /v1/pages/{pageId}/navigate` with `{ "url": "https://..." }` navigates and waits for `readyState=complete`.
- `POST /v1/pages/{pageId}/back`, `/forward`, and `/reload` perform history or reload operations.

## Snapshot and actions

- `GET /v1/pages/{pageId}/snapshot` returns the stable-ref snapshot, readable text, and truncation flags.
- `POST /v1/pages/{pageId}/click` with `{ "ref": "e1" }` clicks a live reference.
- `POST /v1/pages/{pageId}/fill` with `{ "ref": "e1", "value": "..." }` fills a live text control.
- `POST /v1/pages/{pageId}/select` with `{ "ref": "e1", "value": "..." }` selects an option.
- `POST /v1/pages/{pageId}/press` with `{ "key": "Enter" }` sends a key.
- `POST /v1/pages/{pageId}/scroll` with `{ "deltaX": 0, "deltaY": 600 }` scrolls the page.
- `POST /v1/pages/{pageId}/upload` with `{ "ref": "e1", "path": "C:\\..." }` sets a file input selected by a live ref.
- `GET /v1/pages/{pageId}/screenshot` returns PNG bytes.
- `POST /v1/pages/{pageId}/evaluate` with `{ "expression": "..." }` evaluates a page expression and returns its JSON value.
- `POST /v1/pages/{pageId}/wait` accepts `{ "condition": "selector|text|expression", "value": "...", "timeoutMs": 10000 }`.

Stable refs are scoped to a page and document. A navigation, document replacement, or detached element invalidates old refs and returns `409` with code `stale_reference`; callers must request a new snapshot. Input values and password contents never appear in snapshots or event payloads.

## State and diagnostics

- `GET /v1/pages/{pageId}/cookies` and `PUT /v1/pages/{pageId}/cookies` expose cookie state.
- `GET /v1/pages/{pageId}/storage` and `PUT /v1/pages/{pageId}/storage` expose origin-scoped storage state.
- `POST /v1/pages/{pageId}/cdp` with `{ "method": "...", "parameters": { ... } }` is the explicit extension point for request interception, screencast control, and engine diagnostics; its response and events remain scoped to that page.
- `GET /v1/pages/{pageId}/logs` returns bounded console and network diagnostics; live diagnostics are delivered on `/v1/events`.
- Upload, download, request interception, screencast, and manual input are event/API extensions. They must preserve the same token, origin, page identity, and redaction rules.

## Errors

Errors are JSON objects with a stable code and human-readable message: `{ "error": { "code": "...", "message": "..." } }`. The first version uses `400` for malformed input, `401` for a missing or invalid bearer token, `403` for origin/policy rejection, `404` for unknown pages, `409` for stale refs or conflicting state, `408` for condition timeouts, and `500` only for unexpected engine failures.
