# Step Functions

Step functions are .NET code called from JavaScript with the result journaled. Use them for anything with side effects or external state: HTTP calls, database writes, email sends, file I/O.

## Basic registration

```csharp
workflow.RegisterStepFunction("fetchOrder", args =>
    orderService.GetOrder((string)args[0]!));
```

```javascript
const order = await fetchOrder('ORD-001');
```

On first run, the C# function executes; the return value is JSON-serialized and stored in the journal. On replay, the cached value is returned without calling the function again.

## Async steps

```csharp
workflow.RegisterStepFunction("callApi", async (args, ct) =>
{
    var response = await httpClient.GetAsync((string)args[0]!, ct);
    return await response.Content.ReadAsStringAsync(ct);
});
```

The `CancellationToken` flows from `RunWorkflow` / `ResumeWorkflow`, so long-running steps cancel cooperatively when the caller cancels.

## Return types

Step results go through `JSON.stringify` / `JSON.parse` to journal and replay. Anything JSON-serializable works: strings, numbers, booleans, arrays, plain objects. CLR types need to be shaped so `System.Text.Json` can handle them (POCOs, records, dictionaries, etc.).

## Errors

If a step throws, the error is journaled and the script sees a regular JS `Error`:

```javascript
try {
    await callApi('/bad-endpoint');
} catch (e) {
    console.log(e.message);   // the .NET exception's Message
}
```

On replay, the same error is re-thrown deterministically.

## In-process retries with Foundatio.Resilience

Attach a Foundatio resilience policy to retry transient failures within a single execution. The retry loop runs in-process; only the final outcome (success or failure after all attempts) is journaled.

```csharp
using Foundatio.Resilience;

var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .WithJitter()
    .WithShouldRetry((attempt, ex) => ex is HttpRequestException)
    .Build();

workflow.RegisterStepFunction("callApi", impl, policy);
```

Or resolve by name via a provider:

```csharp
var provider = new ResiliencePolicyProviderBuilder()
    .WithPolicy("http", b => b.WithMaxAttempts(5).WithExponentialDelay(TimeSpan.FromSeconds(1)));

workflow.UseResiliencePolicyProvider(provider);
workflow.RegisterStepFunction("callApi", impl, policyName: "http");
```

The policy's `UnhandledExceptions` set is automatically extended to include `RetryableStepException` — see below.

## Durable retries that survive process restarts

Policies only cover retries within one step invocation. For retries spanning minutes to days that should survive a process restart, throw `RetryableStepException`:

```csharp
workflow.RegisterStepFunction("callService", args =>
{
    var response = httpClient.Get((string)args[0]!);
    if (response.StatusCode == 503)
        throw new RetryableStepException("service unavailable", TimeSpan.FromMinutes(5));
    return response.Body;
});
```

The workflow suspends with `SuspensionInfo.ResumeAt` set to `now + RetryAfter`, the orchestrator schedules a resume, and on resume the step re-executes from scratch. The exception **bypasses** any attached resilience policy — it's a workflow-level signal.

## Two retry horizons, two tools

| Need | Tool |
|---|---|
| Flaky network, retry within seconds | Attach an `IResiliencePolicy` |
| Downstream maintenance window, retry in 30 min | Throw `RetryableStepException` |
