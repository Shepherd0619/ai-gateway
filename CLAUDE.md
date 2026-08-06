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
   - Extracts `x-api-key` → returns 401 if missing
   - Parses JSON body, detects client's `"stream": true/false` preference, rewrites `model` field via `ModelMapper`
   - **Non-streaming** (default or `stream: false`): forces `stream: false`, reads full upstream response, rewrites model name back in JSON body, sends as single response
   - **Streaming** (`stream: true`): passes `stream: true` through to upstream, streams SSE response line-by-line, rewrites model name in `data:` lines, flushes after each blank-line SSE event boundary
   - Forwards to the configured upstream (`Upstream:BaseUrl`) with `Authorization: Bearer` and `anthropic-version` headers
   - Writes a compliance log entry (if enabled) via a non-blocking `Channel<T>.TryWrite`

### SSE streaming (`Proxy/ProxyHandler.cs` → `StreamSseResponse`)

- Detects client streaming preference from request body before any mutation
- Uses `HttpCompletionOption.ResponseHeadersRead` for streaming (reads as stream), `ResponseContentRead` for non-streaming
- Upstream non-2xx responses bypass streaming — errors are read as full JSON and returned normally
- Model name rewriting in SSE uses simple string replacement on `data:` lines only (`upstreamModel → originalModel`), guarded by `doRewrite` null-check
- Sets `Content-Type: text/event-stream`, `Cache-Control: no-cache, no-store`, `Connection: keep-alive`
- Compliance log for streaming stores `ResponseBody = "[streaming]"` (can't capture full SSE stream)

### Model mapping (`Proxy/ModelMapper.cs`)

Prefix-based, first-match-wins. Rules come from `appsettings.json` → `ModelMapping:Rules` and are evaluated in order. The `claude-haiku` rule must appear before the `claude` catch-all. No regex — just `string.StartsWith`.

### CORS

A browser CORS policy is configured via `appsettings.json` → `Cors:AllowedOrigins` (array of origin strings, e.g. `["https://pivot.claude.ai"]`). Applied as middleware in `Program.cs`. Configured in `Configuration/CorsOptions.cs`.

### Key constraints

- **Both streaming (SSE) and non-streaming are supported.** Streaming is opt-in via `"stream": true` in the request body.
- **No auth on the proxy itself.** BYOK via `x-api-key` → `Authorization: Bearer`. Deploy on a trusted network.
- **`InvariantGlobalization = true`** in the csproj — no culture-specific behavior.
- **Container base:** `mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled` — distroless, no shell. ASP.NET runtime required for Kestrel.

### Configuration pattern

Uses `IOptions<T>` with records (not classes with setters). All settings overridable via env vars with the `__` separator (e.g. `Upstream__BaseUrl`).

### Compliance logging (`Compliance/`)

Opt-in audit trail. A `BackgroundService` reads from a bounded `Channel<ComplianceEntry>` (capacity 1000, drops oldest when full) and writes JSON-lines to disk. Disabled by default — toggle `ComplianceLog:Enabled` in config.
