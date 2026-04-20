# Continue As New

Long-running and polling workflows shouldn't grow the journal forever. After a natural checkpoint — end of a batch, completion of a polling cycle — call `continueAsNew(...args)` to end the current run and start fresh with an empty journal.

## Basic usage

```javascript
async function poller(cursor) {
    const page = await fetchPage(cursor);
    for (const item of page.items) {
        await process(item);
    }
    if (!page.hasMore) return 'done';
    continueAsNew(page.nextCursor);   // fresh run, carried cursor
}
```

`continueAsNew` is always available — no registration or opt-in. Code after the call does not run; the engine terminates the current execution.

## Caller-side handling

The result's `Status` distinguishes this from completion or suspension:

```csharp
var result = workflow.RunWorkflow(script, "poller", "start");

if (result.Status == WorkflowStatus.ContinuedAsNew)
{
    // A fresh WorkflowState. Same EntryPoint, new RunId, empty journal,
    // new ArgumentsJson from the continueAsNew(...) call.
    scheduler.Schedule(result.State!);
}
```

## Tracing across continuations

Each continuation gets a new `RunId`, but `ParentRunId` chains back to the previous run:

```csharp
result.State.RunId           // new, unique
result.State.ParentRunId     // the run that called continueAsNew
```

Metadata carries through.

## When to use it

- Polling loops that would otherwise accumulate thousands of `fetch` entries
- Long-lived "agent" workflows (watchers, reconcilers) that process one item at a time
- Periodic jobs scheduled for infinite iteration

If your workflow finishes within a bounded number of awaits, you don't need `continueAsNew` — replays will be fast enough.
