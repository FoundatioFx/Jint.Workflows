# HTTP with `fetch`

Jint.Workflows ships an opt-in, browser-compatible `fetch` — the standard WHATWG API for HTTP from JavaScript. The entire request/response is journaled as a single step, so replays return the buffered response without hitting the network.

## Opt-in registration

`fetch` is **not registered by default**. Workflows that don't call `EnableFetch` cannot make HTTP calls — `fetch(...)` is undefined.

```csharp
var http = new HttpClient();

var workflow = new WorkflowEngine()
    .EnableFetch(b => b
        .UseHttpClient(http)
        .UseDefaultTimeout(TimeSpan.FromSeconds(30)));
```

For `IHttpClientFactory` users:

```csharp
.UseHttpClient(factory.CreateClient("jint-workflows"))
```

## Standard fetch API

```javascript
const res = await fetch('https://api.example.com/orders', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sku: 'WIDGET' })
});

if (!res.ok) throw new Error(`HTTP ${res.status}`);

const data = await res.json();
```

Response exposes: `ok`, `status`, `statusText`, `url`, `redirected`, `headers`, `bodyUsed`, `.text()`, `.json()`, and `.clone()`. Header keys are lowercased on the response.

Non-2xx responses **do not throw** — `res.ok` is `false` and you handle it. This matches WHATWG semantics.

## Retries

Attach a Foundatio policy to retry transient failures:

```csharp
using Foundatio.Resilience;

var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .WithJitter()
    .WithShouldRetry((n, ex) => ex is HttpRequestException or TaskCanceledException)
    .Build();

workflow.EnableFetch(b => b
    .UseHttpClient(http)
    .UseDefaultPolicy(policy));
```

## Replay

Each `await fetch(...)` is a single journaled step. On replay, the response is reconstructed from the journal — `.json()` and `.text()` return the cached body. No network call is made.

## Supported subset

Supported today:

- `fetch(url, init)` with string URL
- `init.method`, `init.headers` (plain object, case-insensitive), `init.body` (string or pre-stringified)
- `init.signal` → `CancellationToken`
- `Response.ok`, `status`, `statusText`, `url`, `redirected`, `headers`, `bodyUsed`
- `Response.text()`, `.json()`, `.clone()`

Not yet:

- `Request` / `Response` / `Headers` constructors (use plain objects)
- `.blob()`, `.formData()`, `.arrayBuffer()`
- Streaming body, multipart upload
- `mode`, `credentials`, `cache`, `referrer`, `integrity`, `keepalive`
