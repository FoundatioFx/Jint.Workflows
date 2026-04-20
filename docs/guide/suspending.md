# Suspending Execution

A workflow suspends when it `await`s a registered suspend function. The orchestrator decides when to resume and what value to deliver.

## Custom suspend functions

```csharp
workflow.RegisterSuspendFunction("getApproval");
```

```javascript
const approved = await getApproval('manager', orderId);
```

The workflow pauses. `result.Suspension.FunctionName` is `"getApproval"` and `result.Suspension.Arguments` is `["manager", orderId]` — enough context for the orchestrator to route.

Resume with a payload:

```csharp
var completed = workflow.ResumeWorkflow(script, state, resumeValue: true);
```

The `await` expression receives the payload.

## Computing `ResumeAt`

Pass an optional callback to pre-compute a resume time. Useful for sleep, polling, and retry-after semantics:

```csharp
workflow.RegisterSuspendFunction("sleep", args =>
    DurationParser.Parse(args[0]));
```

`DurationParser.Parse` accepts string durations (`"5d"`, `"2h"`, `"30m"`, `"10s"`) or numeric milliseconds.

```javascript
await sleep('1d');
await sleep(5000);
```

`SuspensionInfo.ResumeAt` tells the orchestrator exactly when to schedule a resume.

## Named external events

Enable `waitForEvent(name, opts?)`:

```csharp
var workflow = new WorkflowEngine().EnableExternalEvents();
```

```javascript
// Single event — returns payload directly
const payment = await waitForEvent('payment-received');

// With timeout — throws TimeoutError if the timeout elapses
try {
    const signal = await waitForEvent('cancel', { timeout: '24h' });
} catch (e) {
    if (e.name === 'TimeoutError') { /* ... */ }
}

// Multiple events — returns { name, payload } so the script can branch
const { name, payload } = await waitForEvent(['payment', 'cancel']);
```

The orchestrator resumes with a named event or a timeout:

```csharp
// Deliver an event
workflow.RaiseEvent(script, state, "payment-received", new { amount = 100 });

// Or expire the waiter
workflow.TimeoutEvent(script, state);
```

`SuspensionInfo.EventNames` exposes the names the workflow is awaiting, so your orchestrator can subscribe the right listeners.

## Fan-out and Promise.race

Standard `Promise.all` and `Promise.race` work over step and suspend calls:

```javascript
// Parallel fetches
const [orders, customers] = await Promise.all([
    fetchOrders(),
    fetchCustomers(),
]);

// Race a payment against a cancel
const result = await Promise.race([
    waitForEvent('payment').then(p => ({ kind: 'paid', p })),
    waitForEvent('cancel').then(() => ({ kind: 'canceled' })),
]);
```

Each call takes the next journal slot in scheduling order. When multiple suspends happen in a single execution, **first-scheduled wins** — the orchestrator observes only the first one, and the rest re-run on subsequent resumes. This keeps replay deterministic.

## Error semantics

- **`Promise.all`** — rejects on first rejection (standard JS).
- **`Promise.race`** — settles with the first-completed promise.
- **Completion wins over suspension.** If `main()` returns while other waiters are still pending (e.g. `Promise.race`), the workflow completes. The pending waiters are abandoned.
