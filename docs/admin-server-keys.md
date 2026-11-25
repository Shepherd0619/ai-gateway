# Server keys: deployment and admin API

The gateway accepts either BYOK provider credentials or gateway-issued `sk-aigw-` keys in `x-api-key` or `Authorization: Bearer`. All valid gateway keys can use all existing model routes. The final route, including classifier routing, determines the backend's provider key. Gateway keys never reach upstream; a failed gateway authentication never falls back to BYOK.

## Configuration and storage

`GatewayKeys:Enabled` defaults to false. Disabled deployments do not create/open a key database, run migrations or require provider credentials. The reserved gateway prefix is rejected even when disabled.

For local development, set `GatewayKeys__Enabled=true`, `Admin__ApiKey`, provider keys, and `ConnectionStrings__GatewayKeys=Data Source=gateway-keys.db`. The SQLite file and auxiliary files are git-ignored. Runtime deployments require a file-backed database; in-memory SQLite is used by the test fixtures only.

Jenkins should supply secrets through its credentials/environment integration, without echoing values or embedding them into the image. Example Compose environment and volume configuration (placeholders only):

```yaml
services:
  gateway:
    image: your-gateway-image
    environment:
      GatewayKeys__Enabled: "true"
      ConnectionStrings__GatewayKeys: "Data Source=/data/gateway-keys.db"
      Admin__ApiKey: "${GATEWAY_ADMIN_KEY:?required}"
      Backends__openrouter__ApiKey: "${OPENROUTER_API_KEY:?required}"
      Backends__openai__ApiKey: "${OPENAI_API_KEY:?required}"
      ForwardedHeaders__Enabled: "true"
      ForwardedHeaders__KnownProxies__0: "${HTTPD_PEER_IP:?required}"
    volumes:
      - /your/persistent/gateway-data:/data
```

Prepare the mounted directory with write permission for the image's runtime user (the .NET chiseled image uses a non-root app user; verify the actual UID for your image). Mount the whole directory: SQLite may create `-wal`, `-shm` and journal files beside the database. SQLite is not encrypted by default; protect the volume and its backups. No separate PostgreSQL service is required.

Provider keys are read from the startup configuration. Temporary admin overrides take precedence, do not enter SQLite/runtime JSON, and disappear on restart. Configuration objects and the container environment still contain provider key plaintext in memory. Deleting an override restores the deployment configuration; it does not disable the backend. A gateway request selecting an unkeyed backend returns 503 `provider_key_not_configured` without forwarding. BYOK continues to use the caller's provider key.

## Client IP behind httpd

The deployment is responsible for httpd restoring the client address behind Cloudflare and forwarding a trustworthy `X-Forwarded-For`. The gateway processes one normalized hop from the explicitly listed `ForwardedHeaders:KnownProxies` addresses. Supply the actual immediate peer address as seen by Kestrel, which may be a container bridge or loopback address rather than the server's public IP. Additional peer addresses use `KnownProxies__1`, etc.

When disabled, the gateway records its direct peer. When enabled with no valid proxy list, startup fails. It does not trust every proxy or directly read `CF-Connecting-IP`/`X-Real-IP`. With a trusted httpd peer, the rightmost `X-Forwarded-For` address becomes `RemoteIpAddress`. Ensure that value is the restored visitor address; do not assume a longer Cloudflare chain will be consumed automatically. An untrusted peer cannot override the recorded address. IPv4-mapped IPv6 is normalized to IPv4; unavailable addresses are null.

## Admin authentication

Every endpoint below requires the existing `x-admin-key`. Without `Admin:ApiKey`, all `/admin/*` routes return 404. Gateway keys cannot administer the gateway. Response JSON uses camelCase; secret-bearing responses use `Cache-Control: no-store`.

### Provider credentials

| Method | Endpoint | Result |
|---|---|---|
| GET | `/admin/backend-credentials` | `{ "backends": [{ "name": "openrouter", "configured": true, "source": "configuration" }] }` |
| PUT | `/admin/backend-credentials/{name}` | Body `{ "apiKey": "new-provider-key" }`; 200 status view |
| DELETE | `/admin/backend-credentials/{name}` | Clear memory override; 200 restored status view |

`source` is `configuration`, `runtime` or `none`. No endpoint returns provider key plaintext, masks or digests. Backend names are case-insensitive and must already exist in `Backends`. Invalid input is 400; unknown backend is 404. For names containing `/`, `%` or path-sensitive text, use PUT/DELETE `/admin/backend-credentials?name=<URL-encoded-name>`. Existing `/admin/backends` still returns only names, so dashboards can join the status by name.

### Gateway keys

These endpoints return 404 while `GatewayKeys:Enabled` is false.

| Method | Endpoint | Result |
|---|---|---|
| POST | `/admin/gateway-keys` | Body `{ "name": "desktop", "expiresAt": null }`; 201 metadata plus one-time `apiKey` |
| GET | `/admin/gateway-keys?limit=50&offset=0` | `{ "keys": [...], "limit": 50, "offset": 0 }` |
| POST | `/admin/gateway-keys/{id}/revoke` | 204, also for an already-revoked key; nonexistent ID 404 |

Metadata: `id`, `name`, `createdAt`, `expiresAt`, `revokedAt`, `lastUsedAt`, `lastUsedIp`. Times are UTC ISO 8601; the last-use fields start as null. Only the issue response includes `apiKey`; neither plaintext nor the stored SHA-256 digest is available in lists. Save the issued key immediately. If the response is lost, list and revoke the record, then issue another. Rotate by issuing a replacement, switching downstream clients and revoking the old key.

Names contain 1–200 characters with no control characters. Optional `expiresAt` must be in the future; input offsets are normalized to UTC. Limit is 1–100, offset is nonnegative. Invalid input is 400. Authentication checks the full high-entropy token digest, expiry and revocation on each request. Requests authenticated before a concurrent revocation may finish, including SSE; later authentications fail.

`lastUsedAt` and `lastUsedIp` represent the most recent successful gateway authentication, including `/v1/models`; upstream failures still count. They are updated together, cannot move backwards, and do not change for failed auth, BYOK or admin operations. Tracking is best-effort: failure to update these metadata fields does not reject an already authenticated request. A null IP overwrites the previous IP when that request is the latest. There is no historical audit table or retention job.

## Discovery and failures

When gateway keys are enabled, a local catalog requires a valid gateway key or a nonempty BYOK credential. BYOK authenticity is checked only on actual upstream calls. Missing/invalid gateway keys return 401; conflicting headers or repeated authentication headers return 400. When gateway keys are disabled, the local catalog remains anonymously readable, except that reserved-prefix credentials are still rejected.

When the local catalog is disabled, `/v1/models` uses the default `openrouter` backend. Gateway mode uses that backend's provider key; BYOK passes the supplied key. This does not aggregate models from all backends.

Runtime database errors return a sanitized 503 for gateway authentication and key administration. Provider credential admin, BYOK and unrelated admin operations continue to work. Keys are not cached and there is no fallback to an ephemeral database. `/health` retains its existing backend reachability meaning; it does not certify key database health.

## Migrations, backup and recovery

EF Core migrations are committed under `Authentication/Migrations`. When gateway keys are enabled, startup applies migrations before accepting traffic. Invalid connections, non-writable volumes or migration failures prevent startup. Startup has a 30-second migration deadline; SQLite operations use a short lock timeout. No request performs DDL, and production does not use `EnsureCreated`.

Before upgrading an existing database, stop the gateway and back up the database and any required SQLite auxiliary files together. Do not copy a live `.db` alone while WAL writes are active. Restore with the gateway stopped and preserve directory permissions. Revoked keys remain revoked after restoration only up to the backup's point in time.

EF Core 9 uses `__EFMigrationsLock` on SQLite. If a migration process was killed and subsequent startup reports a migration lock timeout: stop all gateway/migration processes, back up the files, inspect the schema and `__EFMigrationsHistory` for a partially applied upgrade, then clear only the abandoned migration lock using a SQLite maintenance tool. For a verified abandoned lock, the official recovery operation is:

```sql
DELETE FROM "__EFMigrationsLock";
```

Restart and verify migration completion. Do not delete the key database, automatically clear locks during normal startup, or rebuild an existing database to recover from an error. See [Microsoft's SQLite migration limitations](https://learn.microsoft.com/ef/core/providers/sqlite/limitations#concurrent-migrations-protection).

For future model changes, use a matching EF Core 9.x `dotnet-ef` tool and add a migration, for example `dotnet ef migrations add ChangeGatewayKeys --context GatewayKeyDbContext --output-dir Authentication/Migrations`. The design-time factory uses a local `gateway-keys.db` connection; merely scaffolding migrations does not open it. Changing database engines later requires changing the centralized provider setup, provider-specific migrations and a data transfer; EF Core does not make those automatic.

## Verification

Run `dotnet test Tests/AiGateway.Tests.csproj`. Most database tests use isolated SQLite in-memory connections kept open for each test's lifetime; file-backed tests cover migration/reopen, startup failures and writer locks. Neither production credentials nor production databases are used by tests.
