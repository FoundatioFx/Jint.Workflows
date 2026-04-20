# Jint.Workflows

![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Jint.Workflows/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Jint.Workflows/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Jint.Workflows.svg?style=flat)](https://www.nuget.org/packages/Jint.Workflows/)
[![feedz.io](https://img.shields.io/endpoint?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FJint.Workflows%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Jint.Workflows/latest/download)
[![Discord](https://img.shields.io/discord/715744504891703319)](https://discord.gg/6HxgFCx)

Durable JavaScript workflows for .NET using [Jint](https://github.com/sebastienros/jint). Write long-running orchestration logic as async JavaScript, suspend execution at any `await`, serialize the state, and resume later — potentially days later, in a different process. Uses deterministic replay on top of Jint's public API, with zero engine modifications.

Influenced by Vercel's [Workflow SDK](https://workflow-sdk.dev/), adapted for the .NET / Jint environment.

## ✨ Features

- 💾 **Tiny, portable state** — serialized state is a compact JSON journal of results; store it anywhere
- ⏳ **Suspend for days** — pause at any `await`, persist, resume in a different process
- 🔁 **Deterministic replay** — `Date.now()`, `new Date()`, `Math.random()` overridden for stable replays
- 🛡️ **Journaled side effects** — step functions execute once and replay from cache
- 🩹 **Policy-based retries** — attach Foundatio.Resilience policies for in-process retry, or throw `RetryableStepException` for durable cross-restart retries
- 🌐 **Browser-compatible `fetch`** — opt-in WHATWG `fetch` with `.json()`, `.text()`, `.clone()`
- 📣 **Named external events** — `waitForEvent('name')` single or multi-event, with timeout support
- 🔀 **Fan-out/fan-in** — `Promise.all` and `Promise.race` over steps and suspends
- ♾️ **Continue As New** — restart with a fresh journal to avoid unbounded growth
- 🛠️ **Journal compatibility check** — script drift fails fast with a clear diagnostic

## 🚀 Get Started

```bash
dotnet add package Jint.Workflows
```

```csharp
using Jint.Workflows;

var workflow = new WorkflowEngine();
workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0]));
workflow.RegisterStepFunction("fetchOrder", args => orderService.GetOrder((string)args[0]!));
workflow.RegisterSuspendFunction("getApproval");

var script = """
    async function processOrder(orderId) {
        const order = await fetchOrder(orderId);
        await sleep('3d');
        const approved = await getApproval('manager', orderId);
        return approved ? 'shipped' : 'cancelled';
    }
""";

var result = workflow.RunWorkflow(script, "processOrder", "ORD-001");
// result.Status == Suspended, result.Suspension.ResumeAt ≈ 3 days from now

var json = result.State!.Serialize();  // persist anywhere

// ... 3 days later, in a different process
var resumed = workflow.ResumeWorkflow(script, json);
```

**👉 [Getting Started Guide](https://workflow.foundatio.dev/guide/getting-started)** — step-by-step setup, step functions, suspend functions, and replay.

**📖 [Complete Documentation](https://workflow.foundatio.dev/)**

## 📦 CI Packages (Feedz)

Want the latest CI build before it hits NuGet? Add the Feedz source (read-only public) and install the pre-release version:

```bash
dotnet nuget add source https://f.feedz.io/foundatio/foundatio/nuget -n foundatio-feedz
dotnet add package Jint.Workflows --prerelease
```

Or add to your `NuGet.config`:

```xml
<configuration>
    <packageSources>
        <add key="foundatio-feedz" value="https://f.feedz.io/foundatio/foundatio/nuget" />
    </packageSources>
    <packageSourceMapping>
        <packageSource key="foundatio-feedz">
            <package pattern="Foundatio.*" />
            <package pattern="Jint.Workflows" />
        </packageSource>
    </packageSourceMapping>
</configuration>
```

## 🤝 Contributing

Contributions are welcome! See the [documentation](https://workflow.foundatio.dev/) for how the engine works and what edits are safe across versions.

## 🔗 Related Projects

- **[Jint](https://github.com/sebastienros/jint)** — the JavaScript interpreter this library is built on
- **[Vercel Workflow SDK](https://workflow-sdk.dev/)** — the primary source of inspiration for the replay-based durable workflow model
- **[Azure Durable Functions](https://learn.microsoft.com/en-us/azure/azure-functions/durable/)** — canonical .NET implementation of the deterministic replay pattern
- **[Temporal](https://temporal.io/)** / **[Restate](https://restate.dev/)** — full workflow runtimes with the same replay model at a larger scope

## 📄 License

Apache-2.0 License
