# AI Gateway

[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-BSD%203--Clause-blue.svg)](LICENSE)

A lightweight reverse proxy that lets you use non-Anthropic models with tools that are hard-coded to the Anthropic Messages API. It presents a Claude-compatible model list, rewrites model names in-flight, and forwards everything else to OpenRouter's Anthropic-compatible API.

**Why?** Some AI tools (e.g. Claude Code Co-work) filter available models to Anthropic's own list and won't let you pick anything else — even when the upstream provider speaks the same API format. This proxy sidesteps that limitation without touching the tool itself.

## How it works

```mermaid
flowchart LR
    A[AI Tool<br/>Claude Code, etc.]
    B[AI Gateway<br/>C# proxy]
    C[OpenRouter<br/>Anthropic Skin]
    D[DeepSeek V4 Pro]
    E[DeepSeek V4 Flash]

    A -- "Anthropic Messages API<br/>x-api-key: sk-or-v1-..." --> B
    B -- "Anthropic Messages API<br/>Bearer sk-or-v1-...<br/>model name rewritten" --> C
    C --> D
    C --> E
    B -- "model name rewritten back" --> A
```

1. **Model discovery** — `GET /v1/models` returns a Claude-flavored model list so the client tool sees models it trusts.
2. **Model rewriting** — `claude-sonnet-4-20250514` in the request body becomes `deepseek/deepseek-v4-pro` before it hits OpenRouter. The response model name is rewritten back so the client never notices.
3. **API key passthrough (BYOK)** — The client's `x-api-key` header becomes the `Authorization: Bearer` token sent upstream. You use your own OpenRouter key — no shared keys, no proxy-side auth.

Because OpenRouter's `/api` base already speaks the Anthropic Messages format natively, no protocol translation is needed. Only the model name changes.

## Model mapping

| Client sends | Upstream (OpenRouter) |
|---|---|
| `claude-haiku-4-20250514` | `deepseek/deepseek-v4-flash` |
| `claude-haiku-*` | `deepseek/deepseek-v4-flash` |
| `claude-sonnet-4-20250514` | `deepseek/deepseek-v4-pro` |
| `claude-opus-4-20250514` | `deepseek/deepseek-v4-pro` |
| `claude-*` (catch-all) | `deepseek/deepseek-v4-pro` |

Rules are prefix-matched in order — the `claude-haiku` rule must come before the `claude` catch-all. Mappings are defined in `appsettings.json` and can be customized without code changes.

## Quick start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for local dev)
- An [OpenRouter API key](https://openrouter.ai/keys)

### Run locally

```bash
# Clone
git clone https://github.com/Shepherd0619/ai-gateway.git
cd ai-gateway

# Run (listens on http://0.0.0.0:4000)
dotnet run

# Or with a custom upstream
Upstream__BaseUrl=https://openrouter.ai/api dotnet run
```

### Docker

```bash
docker build -t ai-gateway .
docker run -p 4000:4000 ai-gateway
```

Pre-built images are published to [GitHub Container Registry](https://github.com/Shepherd0619/ai-gateway/pkgs/container/ai-gateway).

### Verify

```bash
# 1. Model discovery
curl http://localhost:4000/v1/models -H "x-api-key: sk-or-v1-YOUR_KEY"

# 2. Send a chat request
curl http://localhost:4000/v1/messages \
  -H "x-api-key: sk-or-v1-YOUR_KEY" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{"model":"claude-sonnet-4-20250514","max_tokens":50,"messages":[{"role":"user","content":"Hello"}]}'

# 3. Point your AI tool at the proxy
export ANTHROPIC_BASE_URL=http://localhost:4000
export ANTHROPIC_AUTH_TOKEN=sk-or-v1-YOUR_KEY
```

## Configuration

All settings live in `appsettings.json`.

| Section | Key | Default | Description |
|---|---|---|---|
| `ModelMapping` | `Rules` | — | Array of `{ "Prefix", "Target" }` objects for model name rewriting |
| `Upstream` | `BaseUrl` | `https://openrouter.ai/api` | Upstream API base URL |
| `ComplianceLog` | `Enabled` | `false` | Enable per-request JSON-line audit log |
| `ComplianceLog` | `Path` | `/var/log/ai-gateway/compliance.log` | Where to write the compliance log |
| `Kestrel` | `Endpoints.Http.Url` | `http://0.0.0.0:4000` | Listen address and port |

All settings can also be overridden with environment variables (e.g. `Upstream__BaseUrl`).

### Custom model mapping example

```json
{
  "ModelMapping": {
    "Rules": [
      { "Prefix": "claude-haiku",  "Target": "openai/gpt-4o-mini" },
      { "Prefix": "claude-sonnet", "Target": "openai/gpt-4o" },
      { "Prefix": "claude",        "Target": "deepseek/deepseek-v4-pro" }
    ]
  }
}
```

## Health check

```
GET /health → {"status":"healthy","upstream":"https://openrouter.ai/api","latency_ms":42}
```

Tests connectivity to the configured upstream with a 5-second timeout. Returns `503` when the upstream is unreachable.

## Project structure

```
ai-gateway/
├── Program.cs                      # App entry point, DI, middleware pipeline
├── Proxy/
│   ├── ProxyHandler.cs             # Core proxy logic: key extraction, model rewrite, forwarding
│   └── ModelMapper.cs              # Prefix-based model name mapping
├── Configuration/
│   ├── MappingRule.cs              # Record: Prefix → Target
│   ├── ModelMappingOptions.cs      # Record: list of MappingRules
│   └── ComplianceLogOptions.cs     # Record: log toggles
├── Discovery/
│   └── ModelDiscoveryEndpoints.cs  # GET /v1/models — spoofed Claude model list
├── Health/
│   └── HealthEndpoints.cs          # GET /health — upstream connectivity check
├── Compliance/
│   ├── ComplianceEntry.cs          # Record: audit log row
│   └── ComplianceLogWriter.cs      # BackgroundService: Channel→file JSON-lines log
├── appsettings.json                # Runtime configuration
├── Dockerfile                      # Multi-stage build (SDK → chiseled runtime)
└── ai-gateway.csproj               # .NET 9 Minimal API, no third-party dependencies
```

## Tech stack

| Layer | Choice |
|---|---|
| Runtime | .NET 9 Minimal API |
| Container | `mcr.microsoft.com/dotnet/runtime-deps:9.0-noble-chiseled` |
| Dependencies | **None** beyond the .NET BCL |
| Docker image | ~35 MB compressed |
| Memory (idle) | ~50-80 MB |

## Limitations

- **No authentication on the proxy itself.** Deploy it on a trusted network (VPN / tailnet) or put a reverse proxy with auth in front of it.
- **OpenRouter-specific.** The proxy assumes an Anthropic-skin compatible upstream. It may work with other providers that speak the same format but hasn't been tested.

## License

BSD 3-Clause.