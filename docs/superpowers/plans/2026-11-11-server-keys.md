# Server Keys Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Implement provider credentials and revocable downstream gateway keys on the feature branch.

**Architecture:** Provider credentials are configuration plus memory-only overrides keyed by existing backend names. EF Core/SQLite stores hashed gateway keys and their last successful authentication time/IP. Shared authentication serves discovery and proxy; trusted forwarding runs before authentication.

**Tech Stack:** .NET 9, EF Core 9.x SQLite, xUnit, SQLite in-memory and temporary-file integration tests.

**Spec:** docs/superpowers/specs/2026-11-11-server-keys-design.md (approved 2026-11-11).

## Global Constraints

- Retain BYOK; reserved sk-aigw- tokens never fall back or reach upstream.
- Provider key overrides never persist; every valid gateway key can use every route.
- Preserve current single-instance routing, classifier, translation and streaming.
- PostgreSQL, access scopes, dashboard implementation and production deployment are out of scope.
- Only last_used_at/last_used_ip are stored; no authentication history table.
- Work on codex/server-keys; preserve pre-existing untracked files.

## Review Focus

- Conflicting/multiple credentials must fail before forwarding, even with discovery enabled.
- Concurrent usage updates must preserve paired time/IP and never undo revocation.
- Runtime database failures must not disable BYOK or expose tokens/SQL parameters.
- Trusted proxy processing must reject spoofed forwarding headers from other peers.
- File-backed migrations must survive repeated startup and reopen; no production memory fallback.

## Task 1: Provider credential snapshots

**Files:** Configuration/BackendConfig.cs; Configuration/BackendConfiguration.cs; Authentication/BackendCredentialStore.cs; Tests/BackendCredentialStoreTests.cs.

**Interfaces:** BackendConfig.ApiKey; BackendCredentialStore.GetKey(string), All, Save(string,string), Delete(string). Views contain only Name/Configured/Source.

- [x] Write tests: case-insensitive override/restoration, no exposed secrets, new store resets overrides, invalid/reserved credential rejection and unknown backend.
- [x] Run `dotnet test Tests/AiGateway.Tests.csproj --filter FullyQualifiedName~BackendCredentialStoreTests`; observe missing feature, then implement and pass.
- [x] Validate configuration without logging values; checkpoint only task-owned files.

## Task 2: EF key persistence and lifecycle

**Files:** Authentication/GatewayKeyDbContext.cs, GatewayKeyStore.cs, GatewayKeyOptions.cs, GatewayKeyDatabase.cs; Authentication/Migrations/*; Tests/GatewayKeyStoreTests.cs; ai-gateway.csproj.

**Interfaces:** GatewayKeyStore.IssueAsync(name,expiresAt,ct), ListAsync(limit,offset,ct), RevokeAsync(id,ct), AuthenticateAsync(token,ct), RecordUseAsync(id,at,ip,ct). Use IDbContextFactory and TimeProvider; store metadata DTO excludes hash. GatewayKeyDatabase registers optional SQLite services and migrates on startup.

- [x] Add compatible EF Core SQLite/Design packages; write tests for issue/one-time secret, hash-only storage, UTC expiration, revocation, monotonic paired usage, file reopen/migration and lock/write failures.
- [x] Run filtered GatewayKeyStoreTests RED, implement entity/model/migration and store, then GREEN.
- [x] Store only SHA-256 of high-entropy token; fixed-time validation, no positive authentication cache, short atomic writes and nontracking queries.
- [x] Verify disabled mode never creates a file and production rejects memory connections; migration errors are sanitized and bounded.

## Task 3: Data-plane authentication, forwarding and IP

**Files:** Authentication/ClientAuthentication.cs, TrustedProxyConfiguration.cs; Proxy/ProxyHandler.cs; Discovery/ModelDiscoveryEndpoints.cs; Program.cs; Tests/ServerKeyRoutingTests.cs, ClientAuthenticationTests.cs, TrustedProxyTests.cs.

**Interfaces:** ClientAuthentication.AuthenticateAsync(HttpContext,bool allowAnonymous=false) returns a cached per-request result (BYOK/gateway/anonymous or writes an error); provider key selection happens after final backend routing.

- [x] Write failing tests for reserved-prefix rejection, conflicting/multiple headers, database outage isolation, discovery modes, provider key switching and classifier routes.
- [x] Implement shared auth; last-use update failure is best-effort after successful validation. BYOK does not touch the database. Missing provider credential returns 503 before upstream send.
- [x] Add explicit trusted proxy configuration; process only X-Forwarded-For from configured immediate proxies (one normalized hop), then record RemoteIpAddress.
- [x] Verify both protocols, streaming, classifier and discovery; run full suite.

## Task 4: Admin contracts, documentation and completion

**Files:** Admin/CredentialEndpoints.cs; Admin/AdminEndpoints.cs; Tests/CredentialEndpointsTests.cs; appsettings.json; README.md; docs/admin-server-keys.md; .gitignore; .dockerignore.

**Interfaces:** /admin/backend-credentials GET, PUT/DELETE with name route/query fallback; /admin/gateway-keys POST/GET and /{id}/revoke POST. Existing admin filter remains authoritative.

- [x] Write failing HTTP endpoint tests for admin guards, one-time secret/list redaction, errors, no-store, pagination, disabled mode and unusual backend names.
- [x] Implement endpoint contracts using earlier store interfaces; preserve existing /admin/backends response.
- [x] Document Jenkins environment injection, SQLite volume/non-root permissions, migrations/backup/lock recovery, trusted httpd IP configuration and dashboard API contracts.
- [x] Run `dotnet test Tests/AiGateway.Tests.csproj`, `dotnet publish -c Release -o out/server-keys`, `git diff --check` and inspect task changes.
- [x] Dispatch one fresh whole-branch reviewer; address important findings with regression tests, commit task-owned files, leave feature branch ready for review without merging/pushing/deploying.

## Execution notes

Native execution is authorized by the user's explicit request to begin implementation; no additional planning approval gate is added. This checkout is retained on the requested feature branch so existing local design files remain available. Record task verification and decisions in .superpowers/sdd/2026-11-11-server-keys/progress.md. The user confirmed httpd already restores client IP; this change configures only the gateway's trusted immediate proxies.

## Verification and handoff

- Full suite: 264 tests passed, including a real Kestrel process issuing/authenticating/revoking a key.
- Release publish passed; git diff checks passed. Linux x64 SQLite native library is present in the portable publish output.
- Independent whole-feature review: no Critical/Important findings. Deferred minor: comma-combined duplicate authentication values return 401 rather than 400; requests still fail before forwarding.
- Runtime SQLite must be file-backed; isolated SQLite memory connections are for tests. No production deployment or load benchmark was performed.
- Existing deeply nested generated bin/obj artifacts were isolated under the git-ignored .superpowers/stale-build-output-20261111 directory; user-owned out and source files were preserved.
- Feature branch remains in the original checkout; no merge or push is performed.
