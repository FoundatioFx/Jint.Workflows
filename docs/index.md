---
layout: home

hero:
  name: Jint Workflows
  text: Durable JavaScript workflows for .NET
  tagline: Write long-running orchestration in JavaScript. Suspend, serialize, and resume — potentially days later, in a different process.
  image:
    src: https://raw.githubusercontent.com/FoundatioFx/Foundatio/main/media/foundatio-icon.png
    alt: Jint Workflows
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/FoundatioFx/Jint.Workflows

features:
  - icon: 💾
    title: Tiny, Portable State
    details: Serialized state is a compact JSON journal of results — no closure snapshots, no opaque heap dumps. Store it anywhere.
    link: /guide/how-it-works
  - icon: ⏳
    title: Suspend for Days
    details: Pause at any `await`, persist, and resume later in a different process. Sleep timers, external events, or manual signals all work the same way.
    link: /guide/suspending
  - icon: 🔁
    title: Deterministic Replay
    details: "On resume, the script re-executes from the start and fast-forwards past journaled operations. `Date.now()`, `new Date()`, and `Math.random()` are overridden for determinism."
    link: /guide/how-it-works
  - icon: 🛡️
    title: Journaled Side Effects
    details: Step functions execute once and replay from cache. Wrap any .NET code — HTTP calls, DB writes, email sends — without worrying about duplicate execution.
    link: /guide/step-functions
  - icon: 🩹
    title: Policy-Based Retries
    details: "Attach a Foundatio.Resilience policy to any step for in-process retry with jitter, backoff, and circuit breaker. Or throw `RetryableStepException` for durable retries across process restarts."
    link: /guide/step-functions
  - icon: 🌐
    title: Browser-Compatible fetch
    details: "Opt-in WHATWG `fetch(url, init)` with `.json()`, `.text()`, and `.clone()`. Journaled responses survive replays — no double-requests."
    link: /guide/fetch
  - icon: 📣
    title: Named External Events
    details: "`waitForEvent('payment-received')` suspends until the caller delivers it. Wait for one name, multiple names, or compose with `Promise.race`."
    link: /guide/suspending
  - icon: ♾️
    title: Continue As New
    details: "Long-running polling workflows shouldn't grow the journal forever. `continueAsNew(...)` hands off to a fresh run with the same entry point."
    link: /guide/continue-as-new
  - icon: 🔨
    title: Zero Engine Modifications
    details: Built entirely on Jint's public API. No forks, no patches — runs against the official Jint 4 package.
---

## Quick Example

```csharp
var workflow = new WorkflowEngine();
workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0]));
workflow.RegisterStepFunction("fetchOrder", args => orderService.GetOrder((string)args[0]!));
workflow.RegisterSuspendFunction("getApproval");

var script = """
    async function processOrder(orderId) {
        const order = await fetchOrder(orderId);   // journaled step
        await sleep('3d');                         // suspends for 3 days
        const approved = await getApproval('manager', orderId);
        return approved ? 'shipped' : 'cancelled';
    }
""";

var result = workflow.RunWorkflow(script, "processOrder", "ORD-001");
// result.Status == Suspended, result.Suspension.ResumeAt == 3 days from now

var json = result.State!.Serialize();            // persist anywhere

// ... 3 days later, in a different process
var resumed = workflow.ResumeWorkflow(script, json);
```
