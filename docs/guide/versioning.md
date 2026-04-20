# Versioning

The script is not part of the serialized state. You pass it on every run and resume, so you can update it between suspensions. This page describes what edits are safe and how the engine detects incompatibilities.

## The rule

The journal records the sequence of awaited operations by `(type, name)`. When the workflow resumes, the script is re-executed and each `await` is matched against the journal position-by-position.

**An edit is safe if the journaled prefix still matches.**

## Safe edits

Anything that doesn't change the sequence of `await`s already in the journal:

- ✅ Adding new code **after** the last journaled operation
- ✅ Fixing bugs in post-journal code
- ✅ Renaming local variables, reformatting, refactoring helpers
- ✅ Changing step function .NET implementations (the cached result is replayed, not the code)
- ✅ Registering new step or suspend functions
- ✅ Changing resilience policies
- ✅ Changing `DurationParser` inputs / sleep logic for future calls
- ✅ Renaming or reorganizing C# classes

## Unsafe edits

Anything that reorders, removes, or renames operations in the journaled prefix:

- ❌ Reordering `await` calls that have already been journaled
- ❌ Removing an `await` that's in the journal
- ❌ Inserting a new `await` before any already-journaled await
- ❌ Renaming a step function whose call is already in the journal
- ❌ Swapping a step for a suspend (or vice versa) at a journaled position
- ❌ Taking a different branch that leads to different `await`s in the journaled prefix

## How the engine detects drift

On replay, each `await` validates that the journal entry at its slot matches:

- **Type match** — step vs. suspend vs. step_error
- **Name match** — the step function name (suspend journal entries don't record a name, so only type is checked)

On mismatch, the engine throws `JournalCompatibilityException` with:

- The slot index where the mismatch was detected
- What the journal said to expect (type + name)
- What the script actually scheduled
- A pointer to this page

The workflow is returned as `WorkflowStatus.Faulted` with the compat exception attached. No cached value is silently returned with the wrong shape.

## Design for change

Two patterns keep you out of trouble:

**1. Add, don't reorder.** When you need new logic, put it after the last await that any in-flight workflow could be past. Old in-flight workflows finish with the old behavior; new workflows get the new logic.

**2. Use `continueAsNew` as a version boundary.** When you reach a natural checkpoint, call `continueAsNew(...)` to start a fresh run with an empty journal. The new run can run on a completely different version of the script.

```javascript
async function processBatch(batchId, version) {
    if (version !== 'v2' && canUpgrade()) {
        continueAsNew(batchId, 'v2');   // restart under v2 semantics
    }
    // ... v2 logic from here ...
}
```

## What about in-flight workflows?

If you discover you need an unsafe change, your options are:

1. **Wait out the fleet.** Keep the old script available until all in-flight workflows drain naturally.
2. **Ship both versions.** Route old state to the old script, new starts to the new one.
3. **Abandon the old state.** For non-critical workflows, discard and re-run.

The engine's fail-fast check means you'll find out at resume time with a clear error — not silently producing wrong results.
