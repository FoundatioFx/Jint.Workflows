# Getting Started

Jint.Workflows lets you write long-running orchestration logic as JavaScript async functions, suspend at any point, persist the state, and resume later — potentially days later, in a different process.

## Install

```bash
dotnet add package Jint.Workflows
```

Requires .NET 10.0+ and [Jint](https://www.nuget.org/packages/Jint) 4.7.1+.

## Hello, workflow

```csharp
using Jint.Workflows;

var workflow = new WorkflowEngine();
workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0]));

var script = """
    async function main() {
        await sleep('5s');
        return 'done';
    }
""";

var result = workflow.RunWorkflow(script, "main");
// result.Status == Suspended
// result.Suspension.FunctionName == "sleep"
// result.Suspension.ResumeAt ≈ 5 seconds from now
```

The workflow suspended at `await sleep('5s')`. It's waiting. Your orchestrator decides when to resume:

```csharp
// Persist the state somewhere
string json = result.State!.Serialize();

// ... 5 seconds later
var resumed = workflow.ResumeWorkflow(script, json);
// resumed.Status == Completed
// resumed.Value.AsString() == "done"
```

## Step functions — journaled side effects

Register a C# function that gets called from JavaScript. The result is cached in the journal so it never re-runs on replay.

```csharp
workflow.RegisterStepFunction("fetchUser", args =>
    userService.GetById((string)args[0]!));
```

```javascript
async function main(userId) {
    const user = await fetchUser(userId);   // runs once, cached on replay
    await sleep('1d');
    return user.name;
}
```

On resume, `fetchUser` returns the cached result without hitting the database again — even though the script re-executes from the top.

## Suspend functions — wait for something external

Register a name that, when awaited, pauses the workflow. The orchestrator chooses when to resume and what value to deliver.

```csharp
workflow.RegisterSuspendFunction("getApproval");
```

```javascript
async function main(orderId) {
    const approved = await getApproval('manager', orderId);
    return approved ? 'shipped' : 'cancelled';
}
```

When execution reaches `await getApproval(...)`, the workflow suspends. The caller resumes with a value:

```csharp
var result = workflow.RunWorkflow(script, "main", "ORD-001");
// result.Suspension.FunctionName == "getApproval"
// result.Suspension.Arguments == ["manager", "ORD-001"]

// ...approval arrives
var completed = workflow.ResumeWorkflow(script, result.State!, resumeValue: true);
// completed.Value.AsString() == "shipped"
```

## Next steps

- [How It Works](./how-it-works) — the replay model and why it's safe
- [Step Functions](./step-functions) — retries, policies, error handling
- [Suspending Execution](./suspending) — sleep, waitForEvent, and custom suspends
- [HTTP with fetch](./fetch) — opt-in browser-compatible fetch
- [Versioning](./versioning) — safe script edits between suspensions
