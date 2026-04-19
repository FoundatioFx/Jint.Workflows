# Jint.Workflows

Durable JavaScript workflows for .NET using [Jint](https://github.com/sebastienros/jint). Write long-running orchestration logic in JavaScript with `await` syntax, suspend execution at any point, serialize the state, and resume later — potentially days later, in a different process.

Built entirely on top of Jint's public API with zero engine modifications.

## How It Works

Jint.Workflows uses **deterministic replay**. When a workflow suspends, the engine records a journal of completed operations. When resumed, the script re-executes from the start, fast-forwarding past completed operations using cached results from the journal, then continues from where it left off.

This means:
- No complex state serialization — just a journal of results
- Scripts can be updated between suspensions
- Step functions (side effects) execute once and replay from cache
- The serialized state is a small JSON document

## Quick Start

```csharp
var workflow = new WorkflowEngine();
workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0]));
workflow.RegisterSuspendFunction("getApproval");

var script = @"
    async function processOrder(orderId) {
        await sleep('3d');
        var approved = await getApproval('manager', orderId);
        return approved ? 'shipped' : 'cancelled';
    }";

// Start the workflow
var result = workflow.RunWorkflow(script, "processOrder", "ORD-001");
// result.Status == Suspended
// result.Suspension.FunctionName == "sleep"
// result.Suspension.ResumeAt == 3 days from now

// Persist the state
string json = result.State!.Serialize();
// Store json in your database...

// ...3 days later, resume
var result2 = workflow.ResumeWorkflow(script, json);
// result2.Suspension.FunctionName == "getApproval"
// result2.Suspension.Arguments == ["manager", "ORD-001"]

// ...manager approves
var result3 = workflow.ResumeWorkflow(script, result2.State!, true);
// result3.Status == Completed
// result3.Value.AsString() == "shipped"
```

## Features

### Suspend Functions

Register custom functions that pause workflow execution when `await`ed. The orchestrator receives the function name and arguments, decides when to resume, and can pass a value back.

```csharp
workflow.RegisterSuspendFunction("waitForPayment");
workflow.RegisterSuspendFunction("getApproval");
workflow.RegisterSuspendFunction("waitForSignal");
```

Pass a second argument — a callback that receives the CLR-converted arguments — to compute a `ResumeAt` timestamp. Return `null` for event-driven suspensions that have no timeout.

```csharp
workflow.RegisterSuspendFunction("waitForSignal", args =>
{
    var timeout = args.Length > 1 ? (TimeSpan)args[1]! : TimeSpan.FromHours(24);
    return DateTimeOffset.UtcNow.Add(timeout);
});
```

```javascript
async function main() {
    var payment = await waitForPayment(invoice.id);
    var approved = await getApproval('finance', payment.amount);
    if (!approved) {
        await waitForSignal('override');
    }
    return 'processed';
}
```

When resuming, pass a value back to the `await` expression:

```csharp
var result = workflow.ResumeWorkflow(script, state, resumeValue: paymentData);
```

### Duration-based Suspensions with `sleep()`

Register `sleep` as a suspend function and delegate `ResumeAt` computation to `DurationParser`:

```csharp
workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0]));
```

Scripts can then use human-readable durations or a millisecond count:

```javascript
await sleep('5d');     // 5 days
await sleep('2h');     // 2 hours
await sleep('30m');    // 30 minutes
await sleep('10s');    // 10 seconds
await sleep(5000);     // 5000 milliseconds
```

The `SuspensionInfo.ResumeAt` property tells the orchestrator exactly when to resume:

```csharp
if (result.Suspension?.ResumeAt is { } resumeAt)
{
    scheduler.ScheduleResume(state, resumeAt);
}
```

Pass a `TimeProvider` to `DurationParser.Parse` for testable time:

```csharp
workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0], timeProvider));
```

### Step Functions (Journaled Side Effects)

Register C# implementations that execute once and are replayed from the journal on subsequent runs. Use these for operations that should not re-execute: HTTP calls, database writes, sending emails, etc.

```csharp
workflow.RegisterStepFunction("fetchOrder", args =>
{
    var id = (string)args[0]!;
    return orderService.GetOrder(id);
});

workflow.RegisterStepFunction("sendEmail", args =>
{
    emailService.Send((string)args[0]!, (string)args[1]!);
    return true;
});
```

```javascript
async function main(orderId) {
    var order = await fetchOrder(orderId);   // Executes once, cached on replay
    await sleep('1d');
    await sendEmail(order.email, 'Ready!');  // Only executes after the sleep
    return 'done';
}
```

On resume, `fetchOrder` returns the cached result without calling `orderService` again.

### Retryable Steps

Step implementations can throw `RetryableStepException` for transient failures. The workflow suspends with a retry hint instead of recording a permanent failure. The step re-executes on the next resume.

```csharp
workflow.RegisterStepFunction("callApi", args =>
{
    var response = httpClient.Get((string)args[0]!);
    if (response.StatusCode == 503)
        throw new RetryableStepException("service unavailable", TimeSpan.FromMinutes(5));
    return response.Body;
});
```

The orchestrator sees `Suspension.ResumeAt` set to 5 minutes from now and can retry automatically.

### Script Versioning

The script is **not stored** in the serialized state. You provide it at both start and resume time. This means you can update the script between suspensions — fix bugs, add behavior after a suspend point — as long as the journal shape remains compatible (same sequence of step/suspend calls up to the resume point).

```csharp
// V1: original script
var result = workflow.RunWorkflow(scriptV1, "main", args);

// V2: updated script with bug fix after the sleep
var result2 = workflow.ResumeWorkflow(scriptV2, result.State!);
```

### Deterministic Replay

`Date.now()`, `new Date()`, and `Math.random()` are overridden for determinism:

- **During replay** (re-executing code before the resume point): `Date.now()` and `new Date()` return the workflow's original start timestamp, and `Math.random()` uses a seeded PRNG. This ensures replay produces identical results.
- **After replay** (executing new code past the journal): `Date.now()` and `new Date()` return real wall-clock time.

### Console Suppression

All `console.*` method calls are suppressed during replay to prevent duplicate log output. If you've set up `console` with a .NET logger (e.g. forwarding to `ILogger`), it will be called only for new code past the journal, not during replay.

```csharp
var workflow = new WorkflowEngine(setup: engine =>
{
    engine.SetValue("console", new
    {
        log = new Action<object>(msg => logger.LogInformation("{Message}", msg)),
        error = new Action<object>(msg => logger.LogError("{Message}", msg)),
    });
});
```

### Run ID

Each workflow run gets a unique `RunId` (GUID), stable across all resumes of the same run. Useful for correlation, logging, and external tracking.

```csharp
var result = workflow.RunWorkflow(script, "main");
var runId = result.State!.RunId; // Same across all resumes of this run
```

### State Serialization

`WorkflowState` serializes to a compact JSON string. Store it anywhere — database, message queue, file system.

```csharp
string json = result.State!.Serialize();

// Later...
var state = WorkflowState.Deserialize(json);
var result = workflow.ResumeWorkflow(script, state);
```

The state includes a format version number. If you try to deserialize a state from a newer version of the library, you get a clear error instead of cryptic failures.

### Engine Configuration

Configure the underlying Jint engine and inject custom .NET functions:

```csharp
var workflow = new WorkflowEngine(
    configure: options =>
    {
        options.TimeoutInterval(TimeSpan.FromSeconds(30));
    },
    setup: engine =>
    {
        engine.SetValue("formatCurrency", new Func<double, string>(
            amount => amount.ToString("C")));
    },
    timeProvider: myTimeProvider  // For testing with FakeTimeProvider
);
```

### SetScript Convenience

If you always use the same script, set it once:

```csharp
workflow.SetScript(script, "main");

var result = workflow.RunWorkflow("order-123");     // No script param needed
var result2 = workflow.ResumeWorkflow(result.State!); // No script param needed
```

## API Reference

### WorkflowEngine

| Method | Description |
|--------|-------------|
| `RegisterSuspendFunction(name, computeResumeAt?)` | Register a function that pauses execution when `await`ed. Optional callback computes `ResumeAt` |
| `RegisterStepFunction(name, implementation)` | Register a journaled C# function |
| `SetScript(script, entryPoint)` | Set the default script and entry function |
| `RunWorkflow(script, entryPoint, args)` | Start a new workflow |
| `ResumeWorkflow(script, state, resumeValue?)` | Resume with explicit script |
| `ResumeWorkflow(state, resumeValue?)` | Resume using `SetScript` script |
| `ResumeWorkflow(serializedState, resumeValue?)` | Resume from JSON string |

### WorkflowResult

| Property | Description |
|----------|-------------|
| `Status` | `Suspended`, `Completed`, or `Faulted` |
| `State` | Serializable state (when suspended) |
| `Suspension` | Function name, arguments, and `ResumeAt` (when suspended) |
| `Value` | Return value as `JsValue` (when completed) |
| `Exception` | Error details (when faulted) |

### WorkflowState

| Property | Description |
|----------|-------------|
| `RunId` | Unique identifier for this workflow run |
| `EntryPoint` | The async function name to call |
| `Journal` | Ordered list of completed operations |
| `Serialize()` | Convert to JSON string |
| `Deserialize(json)` | Parse from JSON string |

## Requirements

- .NET 10.0+
- [Jint](https://www.nuget.org/packages/Jint) 4.7.1+

## Testing

Uses `TimeProvider` for testable time. Pass `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` in tests:

```csharp
var time = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));
var workflow = new WorkflowEngine(timeProvider: time);

var result = workflow.RunWorkflow(script, "main");
// result.Suspension.ResumeAt is exact and predictable

time.Advance(TimeSpan.FromDays(1));
var result2 = workflow.ResumeWorkflow(script, result.State!);
// Date.now() in the script returns the advanced time
```
