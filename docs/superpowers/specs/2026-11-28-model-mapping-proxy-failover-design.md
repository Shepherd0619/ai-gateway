# Model Mapping Proxy Failover Design

## Goal

Allow model mapping and classifier routes to try ordered SOCKS5 proxies when a connection fails or times out, while keeping current single-proxy configuration working.

## Current behavior

`MappingRule` inherits the optional scalar `ProxyServer` property. `ModelMapper` selects the first proxy list while following chained mappings, while `RuntimeClassifierStore` selects the dedicated classifier route. `ProxyHandler` sends one request through the resulting named HTTP client. HTTP error responses are returned to the client; timeout handling is separate from caller cancellation.

## Configuration

Use one canonical ordered `ProxyServers` string array on shared `ModelRoute`, available to mapping rules and classifier configuration, admin runtime mappings, and classifier runtime settings. The scalar `ProxyServer` remains only as a legacy input alias. The classifier admin API uses the same `proxyServers` array field. The first item is the preferred proxy; remaining items are tried in order after eligible connection failures.

```json
{
  "Prefix": "google/",
  "ProxyServers": ["us-exit", "eu-exit", "jp-exit"]
}
```

For chained mappings, the first rule that specifies `ProxyServers` supplies the complete ordered route. An explicitly empty array clears an inherited proxy route and uses the direct backend connection. Duplicate names are tried only once, at their first position.

For compatibility, read legacy scalar `ProxyServer` values for mapping and classifier routes as a one-item list. Treat the scalar as input-only: admin responses and newly persisted mapping/classifier runtime settings emit only canonical `ProxyServers` arrays.

## Request behavior

- Try the selected mapping or classifier route proxies in `ProxyServers` order.
- Move to the next proxy only after `HttpRequestException` or a gateway timeout (`TaskCanceledException` when the client request was not canceled).
- Stop immediately on any upstream HTTP response, including 5xx. Do not retry a request because of an HTTP status.
- Do not fail over when the client cancels the request.
- Create a fresh `HttpRequestMessage` for each attempt, preserving the same URL, body, backend credential, protocol headers, and client identity headers.
- Use existing backend/proxy named clients for mapping and classifier routes; direct routes stay direct when their proxy list is empty.
- If every attempt fails, preserve the current final timeout/connection-failure response behavior.

## Validation and compatibility

- Validate every proxy name in `ProxyServers` against the top-level `ProxyServers` definitions for base configuration and mapping/classifier runtime saves/loads.
- Normalize legacy scalar `ProxyServer` to a one-item route before mapping and serialize it thereafter only as `ProxyServers`.
- Keep mapping or classifier routes without a proxy list routed directly as they are today.
- Keep the canonical list visible through mapping and classifier admin GET/PUT/PATCH and persist it in `mappings-runtime.json` and `classifier-runtime.json`.

## Review focus

- Caller cancellation must not trigger another request.
- A 5xx response must be returned without proxy rotation.
- A connection exception or timeout must advance to the next configured proxy.
- Unknown proxy names must be rejected by startup/runtime validation.
- Rules without a proxy list must retain their existing route and response behavior.
- Legacy mapping and classifier scalar configuration must normalize to a one-item list and emit only the canonical array.
