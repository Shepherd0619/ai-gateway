# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Run locally (listens on http://0.0.0.0:4000)
dotnet run

# Override upstream via env var
Upstream__BaseUrl=https://openrouter.ai/api dotnet run

# Build & publish (Release)
dotnet publish -c Release -o out

# Docker build & run
docker build -t ai-gateway .
docker run -p 4000:4000 ai-gateway

# Verify (model discovery + chat request)
curl http://localhost:4000/v1/models -H "x-api-key: sk-or-v1-YOUR_KEY"
curl http://localhost:4000/v1/messages \
  -H "x-api-key: sk-or-v1-YOUR_KEY" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{"model":"claude-sonnet-4-20250514","max_tokens":50,"messages":[{"role":"user","content":"Hello"}]}'

# Streaming request (SSE)
curl -N http://localhost:4000/v1/messages \
  -H "x-api-key: sk-or-v1-YOUR_KEY" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{"model":"claude-haiku-4-20250514","max_tokens":50,"messages":[{"role":"user","content":"Hello"}],"stream":true}'
```

There are no tests yet.

## Architecture

This is a lightweight reverse proxy that spoofs Anthropic model discovery and rewrites model names so tools locked to the Anthropic API can use non-Anthropic models via OpenRouter. It is a .NET 9 Minimal API with **zero third-party dependencies**.

### Request flow

1. **Model discovery** — `GET /v1/models` (`Discovery/ModelDiscoveryEndpoints.cs`) returns a hardcoded Claude-flavored model list so the client tool trusts the available models.
2. **Proxy** — `POST /v1/{**catchAll}` (`Proxy/ProxyHandler.cs`) does the core work:
   - Extracts API key from `x-api-key` (desktop) or `Authorization: Bearer` (CLI) → returns 401 if missing
   - Reads the request body as raw UTF-8 bytes and parses it once with `JsonDocument` (zero-copy over the bytes); detects the client's `"stream": true/false` preference from the top-level `stream` field
   - **Classifier detection** examines the `system` array for auto-mode classifier signatures and routes matching requests to `Classifier:TargetModel` instead of the main model
   - **Model rewriting** maps `model` field via `ModelMapper` (prefix-based, first-match-wins)
   - Re-serializes the body only when it actually changed (model rewrite / classifier strip), streaming it through `Utf8JsonWriter` and copying untouched fields verbatim via `JsonElement.WriteTo`. Unchanged bodies are forwarded byte-for-byte.
   - **Non-streaming** (default or `stream: false`): reads full upstream response as bytes, rewrites model name back in the JSON body, sends as single response
   - **Streaming** (`stream: true`): passes `stream: true` through to upstream, streams SSE response line-by-line, rewrites model name in `data:` lines, flushes after each blank-line SSE event boundary
   - Forwards to the configured upstream (`Upstream:BaseUrl`) with `Authorization: Bearer` and `anthropic-version` headers
   - Writes a compliance log entry (only if enabled) via a non-blocking `Channel<T>.TryWrite`
   - Log messages include a `[classifier]` role tag for classifier-routed requests

   The request/response bodies are held as bytes (not a `string` → `JsonNode` DOM → re-serialized `string`), so a large body is buffered ~2× instead of ~5–6× — this avoids `OutOfMemoryException` under tight container memory limits.

### SSE streaming (`Proxy/ProxyHandler.cs` → `StreamSseResponse`)

- Detects client streaming preference from request body before any mutation
- Uses `HttpCompletionOption.ResponseHeadersRead` for streaming (reads as stream), `ResponseContentRead` for non-streaming
- Upstream non-2xx responses bypass streaming — errors are read as full JSON and returned normally
- Model name rewriting in SSE uses simple string replacement on `data:` lines only (`upstreamModel → originalModel`), guarded by `doRewrite` null-check
- Sets `Content-Type: text/event-stream`, `Cache-Control: no-cache, no-store`, `Connection: keep-alive`
- Compliance log for streaming stores `ResponseBody = "[streaming]"` (can't capture full SSE stream)

### Classifier detection (`Proxy/ProxyHandler.cs` + `Proxy/ClassifierDetector.cs`)

Claude Code auto-mode sends lightweight security-classifier requests before executing tool calls. These requests share the main model by default, so when the main model is slow or unavailable, auto-mode breaks. The proxy detects classifier requests by matching two system-prompt signatures:

- A system block starting with `x-anthropic-billing-header:`
- A system block starting with `You are a security monitor`

Both must be present in the `system` array. When detected, the request is routed to `Classifier:TargetModel` (e.g. `deepseek-v4-flash`) instead of the main model. The Anthropic-internal `x-anthropic-billing-header` block is stripped before forwarding. Remove the `Classifier:TargetModel` config key to disable.

### Model mapping (`Proxy/ModelMapper.cs`)

Prefix-based, first-match-wins. Rules are evaluated in order. The `claude-haiku` rule must appear before the `claude` catch-all. No regex — just `string.StartsWith`.

Rules come from `RuntimeMappingStore` (a singleton), which merges three sources at startup — highest priority wins:

1. `appsettings.json` → `ModelMapping:Rules` — base rules (committed to git)
2. `mappings-runtime.json` — overrides persisted by the admin API
3. Environment variables (e.g. `ModelMapping__Rules__0__Target`) — docker-compose overrides

The merge is by `Prefix`: a runtime rule with the same `Prefix` as a base rule replaces it; base rules with no runtime override are kept. **Ordering is runtime-first**: the effective list is the runtime rules in array order (the admin UI reorders them via `PUT /admin/mappings`), followed by any base rules not overridden, in base order. This lets a more-specific runtime rule shadow a broader base catch-all. `ModelMapper.Map()` reads the store on every request, so changes take effect immediately with no restart.

Each rule can optionally reference a `ProxyServer` to route requests through a SOCKS5 proxy — useful when the upstream enforces geo-restrictions on certain models.

### Per-model proxy routing (`Proxy/Socks5ConnectCallback.cs` + `Configuration/ProxyServerOptions.cs`)

When OpenRouter blocks models in your region, you can route specific model prefixes through a SOCKS5 proxy. Configure named proxy servers and reference them in model rules:

```json
{
  "ProxyServers": {
    "us-exit": { "Address": "socks5://proxy-host:7890" }
  },
  "ModelMapping": {
    "Rules": [
      { "Prefix": "google/",    "ProxyServer": "us-exit" },
      { "Prefix": "anthropic/", "ProxyServer": "us-exit" }
    ]
  }
}
```

- `ProxyServers` — named proxy server definitions. Each has an `Address` in `socks5://host:port` format. No auth support.
- `ModelMapping.Rules[].ProxyServer` — optional reference to a proxy server name. Omitted/null means direct connection.
- `Target` and `ProxyServer` are independent — a rule can rewrite the model name, route through a proxy, or both.
- Use env vars or `appsettings.Development.json` for the real proxy address — don't commit internal IPs to the public repo.
- The proxy MUST be a SOCKS5 server (no auth). Mihomo/Clash `mixed-port` works.
- Proxy failures are hard errors — no fallback to direct.
- SSE streaming works transparently through the proxy.

### Admin API (`Admin/AdminEndpoints.cs` + `Configuration/RuntimeMappingStore.cs`)

Model mapping rules can be changed at runtime via a REST API under `/admin`, protected by a fixed API key in the `x-admin-key` header (`Admin:ApiKey`).

Endpoints:

- `GET /admin/mappings` — list all effective rules (merged view)
- `GET /admin/mappings/{prefix}` — get a single rule
- `PUT /admin/mappings` — replace all runtime rules
- `PATCH /admin/mappings/{prefix}` — upsert a single rule (prefix from URL takes precedence over body)
- `DELETE /admin/mappings/{prefix}` — remove a runtime override (falls back to base rule)

Key behaviors:

- `RuntimeMappingStore.Save/Upsert/Delete` write to `mappings-runtime.json` (path from `Admin:RuntimeConfigPath`, default `mappings-runtime.json`), then atomically swap the in-memory merged view (`volatile IReadOnlyList<MappingRule>`). Proxy reads are lock-free.
- No API key configured (`Admin:ApiKey` null/empty) → admin API disabled, `/admin/*` returns 404 (endpoint existence not leaked).
- Invalid `ProxyServer` reference → 400 with the name of the undefined proxy server.
- `mappings-runtime.json` is git-ignored and never written back to `appsettings.json`. To "promote" a runtime change to a default, edit `appsettings.json` manually.
- Docker: set `Admin__RuntimeConfigPath` to a writable volume mount (the chiseled `/app` dir is not guaranteed writable).

### CORS

A browser CORS policy is configured via `appsettings.json` → `Cors:AllowedOrigins` (array of origin strings, e.g. `["https://pivot.claude.ai"]`). Applied as middleware in `Program.cs`. Configured in `Configuration/CorsOptions.cs`.

### Key constraints

- **Both streaming (SSE) and non-streaming are supported.** Streaming is opt-in via `"stream": true` in the request body.
- **No auth on the proxy itself.** BYOK via `x-api-key` (desktop) or `Authorization: Bearer` (CLI) → forwarded as `Authorization: Bearer`. Deploy on a trusted network.
- **`InvariantGlobalization = true`** in the csproj — no culture-specific behavior.
- **Container base:** `mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled` — distroless, no shell. ASP.NET runtime required for Kestrel.

### Configuration pattern

Uses `IOptions<T>` with records (not classes with setters). All settings overridable via env vars with the `__` separator (e.g. `Upstream__BaseUrl`).

The exception is model mapping rules, which are mutable at runtime via `RuntimeMappingStore` (a singleton holding a `volatile` merged view, not a static `IOptions` snapshot).

### Compliance logging (`Compliance/`)

Opt-in audit trail. A `BackgroundService` reads from a bounded `Channel<ComplianceEntry>` (capacity 1000, drops oldest when full) and writes JSON-lines to disk. Disabled by default — toggle `ComplianceLog:Enabled` in config.
