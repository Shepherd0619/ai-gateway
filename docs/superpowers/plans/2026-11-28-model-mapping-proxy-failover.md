# Model Mapping Proxy Failover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add ordered proxy failover to model mapping and classifier routes on connection failures and timeouts, with one canonical `ProxyServers` array and input compatibility for legacy scalar `ProxyServer`.

**Architecture:** Normalize mapping rules to an ordered proxy list before runtime mapping and validation. The proxy handler creates a fresh request for each configured proxy and advances only for transport exceptions or gateway timeouts; any HTTP response ends the attempt sequence. Existing named HTTP clients, backend credentials, direct routing, and no-fallback behavior remain intact.

**Tech Stack:** .NET 9 Minimal API, `IConfiguration`, `System.Text.Json`, `HttpClientFactory`, existing xUnit project (no test additions/runs in this execution).

**Spec:** `docs/superpowers/specs/2026-11-28-model-mapping-proxy-failover-design.md`

## Global Constraints

- Keep `ProxyServers` as the single canonical ordered route field for mappings and classifiers; its first item is the preferred proxy.
- Accept legacy scalar `ProxyServer` as a one-item input alias and emit only `ProxyServers`.
- Fail over only on `HttpRequestException` or a gateway timeout; caller cancellation and all HTTP responses stop retries.
- Do not add dependencies; classifier routes use the same failover behavior as mapping routes.
- Preserve existing backend URL, body, credential, and headers across attempts.

## Review Focus

- Caller cancellation must not trigger another attempt.
- Any HTTP response, including 5xx, must stop retries.
- Connection exceptions and gateway timeouts must advance in configured order.
- Invalid proxy names must be rejected at startup, mapping writes, and classifier route saves.
- Legacy scalar input for mapping and classifier routes must normalize and serialize only as the canonical list.

## File Structure

- `Configuration/ModelRoute.cs` — shared canonical proxy list and legacy input alias.
- `Configuration/MappingRule.cs` — mapping rule normalization.
- `Configuration/RuntimeMappingStore.cs` and `Configuration/RuntimeClassifierStore.cs` — normalize legacy input and validate each route list.
- `Program.cs` — startup validation and mapping diagnostics for proxy lists.
- `Admin/AdminEndpoints.cs` — expose the canonical classifier proxy list in the admin response.
- `Proxy/ModelMapper.cs` and `Proxy/BackendRoute.cs` — carry ordered route metadata and select a named client per attempt.
- `Proxy/ProxyHandler.cs` — retry network failures with fresh request messages.
- `README.md` — document the new mapping field and compatibility behavior.

---

### Task 1: Normalize and validate route proxy lists

**Files:**
- Modify: `Configuration/ModelRoute.cs`
- Modify: `Configuration/RuntimeMappingStore.cs`
- Modify: `Configuration/RuntimeClassifierStore.cs`
- Modify: `Program.cs`
- Modify: `Admin/AdminEndpoints.cs`
- Modify: `Proxy/ModelMapper.cs`

**Interfaces:**
- `MappingRule.ProxyServers` is a nullable ordered list. `null` means no explicit proxy choice in this rule; an empty list explicitly clears an inherited route.
- Legacy `ModelRoute.ProxyServer` is input-only compatibility alias; normalization copies it to a one-item list and clears the alias before output/persistence.
- `MapResult` carries the selected backend and complete ordered proxy list.

- [x] Normalize base/runtime mapping rules and base/runtime classifier settings before routing or persistence.
- [x] Validate every proxy name in mapping and classifier lists against configured `ProxyServers`; reject legacy conflicts by preferring the canonical list when both forms are present.
- [x] Preserve first-explicit-route behavior across chained mappings, including an explicit empty list clearing an inherited route.
- [x] Update startup route validation and startup diagnostics to consume the list.
- [x] Review admin GET/PUT/PATCH serialization and runtime JSON persistence to confirm only canonical `ProxyServers` is emitted.

### Task 2: Add ordered transport failover

**Files:**
- Modify: `Proxy/BackendRoute.cs`
- Modify: `Proxy/ProxyHandler.cs`

**Interfaces:**
- `BackendRoute.Create` accepts the proxy name for one attempt and continues to derive the backend URL/client name from the selected backend.
- Request construction is repeated per attempt so each `HttpRequestMessage` is fresh.

- [x] Try the selected mapping or classifier route proxy names in order, keeping URL, body, credentials, and headers constant.
- [x] On `HttpRequestException` or timeout (`TaskCanceledException` while the client token is not canceled), continue only if another proxy remains.
- [x] On caller cancellation, propagate without trying another proxy.
- [x] On any `HttpResponseMessage`, stop immediately and preserve existing response handling.
- [x] If all attempts fail, preserve existing final timeout/connection-failure behavior.

### Task 3: Document configuration

**Files:**
- Modify: `README.md`

- [x] Document the `ProxyServers` array and explain first-item, fallback order, and classifier routing.
- [x] Document that legacy scalar input is accepted and canonical output is the array.
- [x] Run `dotnet build` from the repository root and inspect the final diff/status; do not run the test project in this execution.
