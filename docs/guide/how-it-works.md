# How It Works

Jint.Workflows uses **deterministic replay**. This page explains the model so you can reason about what's safe and what's not.

## The journal

Every `await` of a registered step or suspend produces a journal entry. The journal is just a list of `{ type, name, resultJson }` tuples recorded in the order the script awaits them.

When the script is suspended, the journal is serialized as JSON. When resumed, the script re-executes from the top — every `await` that has a matching journal entry returns the cached result immediately instead of re-running.

```
Journal: [
  { type: "step",    name: "fetchOrder",  result: {"id":"ORD-001",...} },
  { type: "suspend", name: "",            result: true  },   // resume value
  { type: "step",    name: "sendEmail",   result: true  }
]
```

On replay, the script hits `await fetchOrder(...)` → journal slot 0 → returns the cached order; `await sleep(...)` → journal slot 1 → returns `true`; `await sendEmail(...)` → journal slot 2 → returns `true`. Once the journal is exhausted, new `await`s actually execute.

## What gets stored

```csharp
class WorkflowState
{
    int    Version;
    string EntryPoint;       // the async function to invoke
    string ArgumentsJson;    // initial arguments
    List<JournalEntry> Journal;
    string RunId;            // stable across resumes
    long   StartedAtMs;      // for deterministic Date.now()
    Dictionary<string,string> Metadata;
    string? ParentRunId;     // set by continueAsNew
}
```

Notably **the script is not stored**. You pass it on every resume. This means you can deploy bug fixes and new code between suspensions — as long as the journaled prefix still matches.

## Why this works

Three properties make replay safe:

1. **Steps are pure from the journal's perspective.** Even if the underlying .NET code has side effects, only the first execution runs them. Replays return cached results.
2. **Time is pinned.** `Date.now()`, `new Date()`, and `Math.random()` are overridden to return deterministic values during replay. After the journal runs out, they return real wall-clock time and fresh randomness.
3. **Console output is suppressed during replay.** If you wire `console.log` to a .NET logger, it won't double-log on each resume.

## Cancellation, cost, and restrictions

Replay has a cost: the script re-executes from the start every time. For most orchestrations this is negligible (milliseconds) because the expensive work — HTTP calls, database writes — is cached in the journal. But for workflows with thousands of steps, the journal grows, and replay time grows linearly.

That's what [`continueAsNew`](./continue-as-new) is for: restart the workflow with a fresh, empty journal when you've hit a natural checkpoint.

Script code between `await`s should be:

- **Deterministic.** Avoid time, randomness, or I/O that isn't a step function. Use `Math.random()` and `Date` safely (they're already overridden during replay).
- **Idempotent on re-execution.** Pure computation on journaled inputs is fine. Side effects must go through step or suspend functions.

See [Versioning](./versioning) for what script edits are safe between suspensions.
