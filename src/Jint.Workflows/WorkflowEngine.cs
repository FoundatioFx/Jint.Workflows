using System.Text.Json;
using Foundatio.Resilience;
using Jint.Native;
using Jint.Runtime.Interop;
using Jint.Workflows.Fetch;

namespace Jint.Workflows;

/// <summary>
/// Executes JavaScript workflows that can be suspended at registered suspend functions
/// and resumed later, potentially in a different process. Uses deterministic replay
/// with a journal of completed operations.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly Action<Options>? _configure;
    private readonly Action<Engine>? _setup;
    private readonly Action<Engine>? _postSetup;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Func<object?[], DateTimeOffset?>?> _suspendFunctions = new();
    private readonly Dictionary<string, Func<object?[], object?>> _stepFunctions = new();
    private readonly Dictionary<string, Func<object?[], CancellationToken, Task<object?>>> _asyncStepFunctions = new();
    private readonly Dictionary<string, StepPolicyBinding> _stepPolicies = new();
    private readonly List<Action<Engine>> _engineSetupActions = new();
    private IResiliencePolicyProvider? _policyProvider;
    private string? _script;
    private string? _entryPoint;
    private WorkflowTracker? _currentTracker;

    /// <summary>
    /// Whether the currently executing workflow is replaying its journal (pre-suspension history).
    /// Returns <c>false</c> outside of execution or when no workflow is in flight.
    /// </summary>
    public bool IsReplaying => _currentTracker?.IsReplaying ?? false;

    /// <param name="configure">Optional engine configuration (timeouts, CLR access, etc.).</param>
    /// <param name="setup">Optional per-execution engine setup. Runs before suspend/step functions are installed.</param>
    /// <param name="postSetup">Optional callback that runs after all internal functions (suspend, step, sleep)
    /// are installed but before the script executes. Use this to register wrapper objects that
    /// reference suspend functions.</param>
    /// <param name="timeProvider">Optional time provider for testing. Defaults to <see cref="TimeProvider.System"/>.</param>
    public WorkflowEngine(Action<Options>? configure = null, Action<Engine>? setup = null, Action<Engine>? postSetup = null, TimeProvider? timeProvider = null)
    {
        _configure = configure;
        _setup = setup;
        _postSetup = postSetup;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Set the JavaScript source and entry function for this workflow engine.
    /// </summary>
    public WorkflowEngine SetScript(string script, string entryPoint)
    {
        _script = script;
        _entryPoint = entryPoint;
        return this;
    }

    /// <summary>
    /// Evaluate JavaScript code on each fresh engine before the workflow script runs.
    /// Use this to load library scripts, define helper functions, etc.
    /// Called in order after the constructor's <c>setup</c> callback.
    /// </summary>
    public WorkflowEngine Execute(string code)
    {
        _engineSetupActions.Add(engine => engine.Evaluate(code));
        return this;
    }

    /// <summary>
    /// Set a global value on each fresh engine before the workflow script runs.
    /// Use this to inject .NET objects, functions, or constants into the script environment.
    /// Called in order after the constructor's <c>setup</c> callback.
    /// </summary>
    public WorkflowEngine SetValue(string name, object value)
    {
        _engineSetupActions.Add(engine => engine.SetValue(name, value));
        return this;
    }

    /// <summary>
    /// Register a function that pauses workflow execution when <c>await</c>ed.
    /// </summary>
    /// <param name="name">The JavaScript function name.</param>
    /// <param name="computeResumeAt">
    /// Optional callback that receives the CLR-converted arguments and returns a
    /// <see cref="DateTimeOffset"/> at which the workflow should be automatically resumed.
    /// Return <c>null</c> for event-driven suspensions with no timeout.
    /// </param>
    public void RegisterSuspendFunction(string name, Func<object?[], DateTimeOffset?>? computeResumeAt = null)
    {
        _suspendFunctions[name] = computeResumeAt;
    }

    /// <summary>
    /// Register a synchronous step function that journals its result.
    /// On replay, the cached result is returned without re-executing.
    /// </summary>
    public void RegisterStepFunction(string name, Func<object?[], object?> implementation)
    {
        _stepFunctions[name] = implementation;
        _stepPolicies.Remove(name);
    }

    /// <summary>
    /// Register a synchronous step function with an in-process resilience policy
    /// (Foundatio.Resilience). Retries happen within a single step execution — if the
    /// policy exhausts attempts, the step's failure is journaled.
    /// <para>
    /// For retries that should survive process restarts, throw
    /// <see cref="RetryableStepException"/> from the implementation instead —
    /// it bypasses the policy and suspends the workflow with <c>ResumeAt</c>.
    /// </para>
    /// </summary>
    public void RegisterStepFunction(string name, Func<object?[], object?> implementation, IResiliencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _stepFunctions[name] = implementation;
        _stepPolicies[name] = new StepPolicyBinding(policy, null);
        BypassRetryableStepException(policy);
    }

    /// <summary>
    /// Register a synchronous step function that resolves its resilience policy
    /// from the configured <see cref="IResiliencePolicyProvider"/> at execution time.
    /// Call <see cref="UseResiliencePolicyProvider"/> to supply the provider.
    /// </summary>
    public void RegisterStepFunction(string name, Func<object?[], object?> implementation, string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        _stepFunctions[name] = implementation;
        _stepPolicies[name] = new StepPolicyBinding(null, policyName);
    }

    /// <summary>
    /// Register an async step function that journals its result.
    /// On replay, the cached result is returned without re-executing.
    /// Receives a <see cref="CancellationToken"/> for cooperative cancellation.
    /// </summary>
    public void RegisterStepFunction(string name, Func<object?[], CancellationToken, Task<object?>> implementation)
    {
        _asyncStepFunctions[name] = implementation;
        _stepPolicies.Remove(name);
    }

    /// <summary>
    /// Register an async step function with an in-process resilience policy
    /// (Foundatio.Resilience). See the sync overload for retry semantics.
    /// </summary>
    public void RegisterStepFunction(string name, Func<object?[], CancellationToken, Task<object?>> implementation, IResiliencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _asyncStepFunctions[name] = implementation;
        _stepPolicies[name] = new StepPolicyBinding(policy, null);
        BypassRetryableStepException(policy);
    }

    /// <summary>
    /// Register an async step function that resolves its resilience policy
    /// from the configured <see cref="IResiliencePolicyProvider"/> at execution time.
    /// </summary>
    public void RegisterStepFunction(string name, Func<object?[], CancellationToken, Task<object?>> implementation, string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        _asyncStepFunctions[name] = implementation;
        _stepPolicies[name] = new StepPolicyBinding(null, policyName);
    }

    /// <summary>
    /// Supply a resilience policy provider used to resolve step policies registered by name.
    /// </summary>
    public WorkflowEngine UseResiliencePolicyProvider(IResiliencePolicyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _policyProvider = provider;
        return this;
    }

    /// <summary>
    /// Register a <c>waitForEvent(nameOrNames, { timeout })</c> function for
    /// named external-event suspensions. Pass either a single string or an
    /// array of names; the workflow suspends until the caller invokes
    /// <c>RaiseEvent</c> or <c>TimeoutEvent</c>.
    /// <para>
    /// Single-event form returns the payload directly. Multi-event form
    /// returns <c>{ name, payload }</c> so the script can branch on which
    /// event fired.
    /// </para>
    /// <para>
    /// Resume with <c>RaiseEvent(...)</c> to deliver an event, or <c>TimeoutEvent(...)</c>
    /// to cause the script's <c>waitForEvent</c> to throw a <c>TimeoutError</c>.
    /// </para>
    /// </summary>
    public WorkflowEngine EnableExternalEvents()
    {
        RegisterSuspendFunction(WorkflowTracker.WaitForEventSuspendName, args =>
        {
            if (args.Length > 1 && args[1] is string timeoutStr && !string.IsNullOrEmpty(timeoutStr))
            {
                return DurationParser.Parse(timeoutStr, _timeProvider);
            }
            return null;
        });

        Execute(ExternalEventsPolyfill.Source);
        return this;
    }

    /// <summary>
    /// Resume a workflow suspended on <c>waitForEvent</c>, delivering a named event.
    /// The script's <c>waitForEvent</c> returns the payload (single-event form) or
    /// <c>{ name, payload }</c> (multi-event form).
    /// </summary>
    public WorkflowResult RaiseEvent(WorkflowState state, string eventName, object? payload = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        return ResumeWorkflow(state, new { name = eventName, payload }, cancellationToken);
    }

    /// <inheritdoc cref="RaiseEvent(WorkflowState, string, object?, CancellationToken)" />
    public WorkflowResult RaiseEvent(string script, WorkflowState state, string eventName, object? payload = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        return ResumeWorkflow(script, state, new { name = eventName, payload }, cancellationToken);
    }

    /// <summary>
    /// Resume a workflow suspended on <c>waitForEvent</c> with a timeout.
    /// The script's <c>waitForEvent</c> throws a <c>TimeoutError</c>.
    /// </summary>
    public WorkflowResult TimeoutEvent(WorkflowState state, CancellationToken cancellationToken = default)
    {
        return ResumeWorkflow(state, new { __timeout = true }, cancellationToken);
    }

    /// <inheritdoc cref="TimeoutEvent(WorkflowState, CancellationToken)" />
    public WorkflowResult TimeoutEvent(string script, WorkflowState state, CancellationToken cancellationToken = default)
    {
        return ResumeWorkflow(script, state, new { __timeout = true }, cancellationToken);
    }

    /// <summary>
    /// Register a browser-compatible <c>fetch</c> function. Workflows that don't
    /// call this method cannot make HTTP calls — <c>fetch</c> is undefined.
    /// <para>
    /// Internally registers a journaled async step (<c>__wf_fetch_internal</c>) and
    /// injects a JS polyfill that wraps the step result in a <c>Response</c>-like
    /// object with <c>.json()</c>, <c>.text()</c>, and <c>.clone()</c> methods.
    /// </para>
    /// </summary>
    public WorkflowEngine EnableFetch(Action<FetchBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new FetchBuilder();
        configure(builder);

        // Eager validation — throws here if neither client source was configured.
        _ = builder.ResolveClient();

        Task<object?> Impl(object?[] args, CancellationToken ct) =>
            FetchStep.ExecuteAsync(builder, args, ct);

        if (builder.DefaultPolicy is not null)
        {
            RegisterStepFunction("__wf_fetch_internal", Impl, builder.DefaultPolicy);
        }
        else
        {
            RegisterStepFunction("__wf_fetch_internal", Impl);
        }

        Execute(FetchPolyfill.Source);
        return this;
    }

    private static void BypassRetryableStepException(IResiliencePolicy policy)
    {
        // RetryableStepException signals "retry at the workflow level, across process restarts."
        // It must escape any in-process policy immediately.
        if (policy is ResiliencePolicy concrete)
        {
            concrete.UnhandledExceptions.Add(typeof(RetryableStepException));
        }
    }

    /// <summary>
    /// Start a new workflow using the script set via <see cref="SetScript"/>.
    /// </summary>
    public WorkflowResult RunWorkflow(params object?[] args)
    {
        if (_script is null || _entryPoint is null)
            throw new InvalidOperationException("Call SetScript() before RunWorkflow(), or use the overload that accepts a script.");

        return RunWorkflow(_script, _entryPoint, args);
    }

    /// <summary>
    /// Start a new workflow by executing the given script's async entry function.
    /// </summary>
    public WorkflowResult RunWorkflow(string script, string entryPoint, params object?[] args)
    {
        return RunWorkflow(script, entryPoint, CancellationToken.None, args);
    }

    /// <summary>
    /// Start a new workflow with cancellation support.
    /// </summary>
    public WorkflowResult RunWorkflow(string script, string entryPoint, CancellationToken cancellationToken, params object?[] args)
    {
        var argumentsJson = JsonSerializer.Serialize<object?[]>(args);
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var state = new WorkflowState(
            entryPoint,
            argumentsJson,
            new List<JournalEntry>(),
            runId,
            startedAt);

        return ExecuteWorkflow(script, state, cancellationToken);
    }

    /// <summary>
    /// Start a new workflow, passing CLR objects directly as function arguments
    /// without JSON serialization. Use this when arguments contain .NET objects
    /// with methods that must remain callable from JavaScript (e.g., script contexts).
    /// The caller must provide the same arguments on resume via <see cref="ResumeWorkflow(WorkflowState, object?[], object?, CancellationToken)"/>.
    /// </summary>
    public WorkflowResult RunWorkflow(string script, string entryPoint, object?[] liveArgs, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var state = new WorkflowState(
            entryPoint,
            "[]",
            new List<JournalEntry>(),
            runId,
            startedAt);

        return ExecuteWorkflow(script, state, cancellationToken, liveArgs);
    }

    /// <summary>
    /// Resume a suspended workflow using the script set via <see cref="SetScript"/>.
    /// </summary>
    public WorkflowResult ResumeWorkflow(WorkflowState state, object? resumeValue = null, CancellationToken cancellationToken = default)
    {
        if (_script is null)
            throw new InvalidOperationException("Call SetScript() before ResumeWorkflow(), or use the overload that accepts a script.");

        return ResumeWorkflow(_script, state, resumeValue, cancellationToken);
    }

    /// <summary>
    /// Resume a suspended workflow with an explicit script.
    /// </summary>
    public WorkflowResult ResumeWorkflow(string script, WorkflowState state, object? resumeValue = null, CancellationToken cancellationToken = default)
    {
        if (!state.IsRetry)
        {
            var resumeJson = resumeValue is not null
                ? JsonSerializer.Serialize(resumeValue)
                : null;
            state.Journal.Add(new JournalEntry("suspend", "", resumeJson));
        }
        else
        {
            state.IsRetry = false;
        }
        return ExecuteWorkflow(script, state, cancellationToken);
    }

    /// <summary>
    /// Resume a suspended workflow, passing live CLR objects as function arguments.
    /// Use this when the workflow was started with <see cref="RunWorkflow(string, string, object?[], CancellationToken)"/>.
    /// </summary>
    public WorkflowResult ResumeWorkflow(WorkflowState state, object?[] liveArgs, object? resumeValue = null, CancellationToken cancellationToken = default)
    {
        if (_script is null)
            throw new InvalidOperationException("Call SetScript() before ResumeWorkflow(), or use the overload that accepts a script.");

        return ResumeWorkflow(_script, state, liveArgs, resumeValue, cancellationToken);
    }

    /// <summary>
    /// Resume a suspended workflow with an explicit script, passing live CLR objects as function arguments.
    /// </summary>
    public WorkflowResult ResumeWorkflow(string script, WorkflowState state, object?[] liveArgs, object? resumeValue = null, CancellationToken cancellationToken = default)
    {
        if (!state.IsRetry)
        {
            var resumeJson = resumeValue is not null
                ? JsonSerializer.Serialize(resumeValue)
                : null;
            state.Journal.Add(new JournalEntry("suspend", "", resumeJson));
        }
        else
        {
            state.IsRetry = false;
        }
        return ExecuteWorkflow(script, state, cancellationToken, liveArgs);
    }

    /// <summary>
    /// Resume a suspended workflow from a serialized state string.
    /// </summary>
    public WorkflowResult ResumeWorkflow(string serializedState, object? resumeValue = null, CancellationToken cancellationToken = default)
    {
        var state = WorkflowState.Deserialize(serializedState);
        return ResumeWorkflow(state, resumeValue, cancellationToken);
    }

    /// <summary>
    /// Resume a suspended workflow from a serialized state string with an explicit script.
    /// </summary>
    public WorkflowResult ResumeWorkflow(string script, string serializedState, object? resumeValue = null, CancellationToken cancellationToken = default)
    {
        var state = WorkflowState.Deserialize(serializedState);
        return ResumeWorkflow(script, state, resumeValue, cancellationToken);
    }

    private WorkflowResult ExecuteWorkflow(string script, WorkflowState state, CancellationToken cancellationToken, object?[]? liveArgs = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = CreateEngine(cancellationToken);

        // Deserialize journal values for replay
        var journalValues = new List<JsValue>(state.Journal.Count);
        foreach (var entry in state.Journal)
        {
            journalValues.Add(entry.ResultJson is not null
                ? ParseJsonToJsValue(engine, entry.ResultJson)
                : JsValue.Undefined);
        }

        var tracker = new WorkflowTracker(state.Journal, journalValues, _stepFunctions, _asyncStepFunctions, _suspendFunctions, _stepPolicies, _policyProvider, _timeProvider, cancellationToken);
        _currentTracker = tracker;

        try
        {
        InstallDeterministicBuiltins(engine, tracker, state.RunId, state.StartedAtMs);
        InstallConsoleSuppression(engine, tracker);

        foreach (var name in _suspendFunctions.Keys)
        {
            engine.SetValue(name, tracker.CreateSuspendFunction(engine, name));
        }

        foreach (var name in _stepFunctions.Keys)
        {
            engine.SetValue(name, tracker.CreateStepFunction(engine, name));
        }

        foreach (var name in _asyncStepFunctions.Keys)
        {
            engine.SetValue(name, tracker.CreateStepFunction(engine, name));
        }

        _postSetup?.Invoke(engine);

        // Load as module if source contains export, otherwise evaluate as script
        JsValue entryFn;
        if (script.Contains("export "))
        {
            engine.Modules.Add("__wf_main__", script);
            var module = engine.Modules.Import("__wf_main__");
            entryFn = module.Get(state.EntryPoint);
        }
        else
        {
            engine.Evaluate(script);
            entryFn = engine.GetValue(state.EntryPoint);
        }

        var jsArgs = liveArgs is not null
            ? liveArgs.Select(a => a is not null ? JsValue.FromObject(engine, a) : JsValue.Null).ToArray()
            : DeserializeArgs(engine, state.ArgumentsJson);

        string? completionStatus = null;
        JsValue completionValue = JsValue.Undefined;

        engine.SetValue("__wf_onDone", new ClrFunction(engine, "onDone", (_, args) =>
        {
            completionStatus = "fulfilled";
            completionValue = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        }));

        engine.SetValue("__wf_onFail", new ClrFunction(engine, "onFail", (_, args) =>
        {
            completionStatus = "rejected";
            completionValue = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        }));

        engine.SetValue("__wf_entry", entryFn);
        engine.SetValue("__wf_args", jsArgs);

        engine.Evaluate(@"
            (async function() {
                try {
                    var result = await __wf_entry.apply(null, __wf_args);
                    __wf_onDone(result === undefined ? null : result);
                } catch(e) {
                    __wf_onFail(e);
                }
            })();
        ");

        engine.Advanced.ProcessTasks();

        // Completion wins over suspension: Promise.race can resolve main() even
        // when other parallel waiters are still pending. Abandon them.
        if (string.Equals(completionStatus, "fulfilled", StringComparison.Ordinal))
        {
            return WorkflowResult.Completed(completionValue);
        }

        if (string.Equals(completionStatus, "rejected", StringComparison.Ordinal))
        {
            return WorkflowResult.Faulted(new WorkflowFaultedException(completionValue));
        }

        if (tracker.IsSuspended)
        {
            var newState = new WorkflowState(
                state.EntryPoint,
                state.ArgumentsJson,
                new List<JournalEntry>(tracker.NewJournal),
                state.RunId,
                state.StartedAtMs,
                metadata: state.Metadata)
            {
                IsRetry = tracker.CurrentSuspension!.IsRetry
            };

            return WorkflowResult.Suspended(newState, tracker.CurrentSuspension!);
        }

        return WorkflowResult.Faulted(new InvalidOperationException(
            "Workflow did not complete or suspend. Ensure the entry function is async."));
        }
        finally
        {
            _currentTracker = null;
        }
    }

    private void InstallDeterministicBuiltins(Engine engine, WorkflowTracker tracker, string runId, long startedAtMs)
    {
        var nowFn = new ClrFunction(engine, "__wf_now", (_, _) =>
        {
            if (tracker.IsReplaying)
                return JsValue.FromObject(engine, startedAtMs);
            return JsValue.FromObject(engine, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        });
        engine.SetValue("__wf_now", nowFn);

        var seed = runId.GetHashCode();
        engine.Evaluate($@"
            (function() {{
                var __OrigDate = Date;
                var __wf_seed = {seed};

                function WorkflowDate() {{
                    if (arguments.length === 0) {{
                        return new __OrigDate(__wf_now());
                    }}
                    var args = Array.prototype.slice.call(arguments);
                    return new (Function.prototype.bind.apply(__OrigDate, [null].concat(args)))();
                }}
                WorkflowDate.prototype = __OrigDate.prototype;
                WorkflowDate.now = function() {{ return __wf_now(); }};
                WorkflowDate.parse = __OrigDate.parse;
                WorkflowDate.UTC = __OrigDate.UTC;
                Date = WorkflowDate;

                Math.random = function() {{
                    __wf_seed ^= __wf_seed << 13;
                    __wf_seed ^= __wf_seed >> 17;
                    __wf_seed ^= __wf_seed << 5;
                    return (__wf_seed >>> 0) / 4294967296;
                }};
            }})();
        ");
    }

    private static void InstallConsoleSuppression(Engine engine, WorkflowTracker tracker)
    {
        var consoleVal = engine.Evaluate("typeof console !== 'undefined' ? console : null");

        var originals = new Dictionary<string, JsValue>();
        if (!consoleVal.IsNull() && !consoleVal.IsUndefined())
        {
            var consoleObj = consoleVal.AsObject();
            foreach (var entry in consoleObj.GetOwnProperties())
            {
                var value = entry.Value.Value;
                if (!value.IsUndefined() && !value.IsNull())
                {
                    originals[entry.Key.ToString()] = value;
                }
            }
        }

        engine.Evaluate("var console = {}");
        var newConsole = engine.Evaluate("console").AsObject();

        foreach (var (name, original) in originals)
        {
            var orig = original;
            var wrapper = new ClrFunction(engine, name, (_, args) =>
            {
                if (!tracker.IsReplaying)
                {
                    orig.Call(JsValue.Undefined, args);
                }
                return JsValue.Undefined;
            });
            newConsole.Set(name, wrapper);
        }
    }

    private Engine CreateEngine(CancellationToken cancellationToken = default)
    {
        var engine = new Engine(options =>
        {
            _configure?.Invoke(options);

            if (cancellationToken.CanBeCanceled)
            {
                options.CancellationToken(cancellationToken);
            }
        });

        _setup?.Invoke(engine);

        foreach (var action in _engineSetupActions)
        {
            action(engine);
        }

        return engine;
    }

    internal static JsValue ParseJsonToJsValue(Engine engine, string json)
    {
        engine.SetValue("__wf_json", json);
        return engine.Evaluate("JSON.parse(__wf_json)");
    }

    internal static string? JsValueToJson(Engine engine, JsValue value)
    {
        engine.SetValue("__wf_val", value);
        var result = engine.Evaluate("JSON.stringify(__wf_val)");
        return result.IsUndefined() ? null : result.AsString();
    }

    private static JsValue[] DeserializeArgs(Engine engine, string argumentsJson)
    {
        var jsArray = ParseJsonToJsValue(engine, argumentsJson);
        if (jsArray.IsNull() || jsArray.IsUndefined())
        {
            return [];
        }

        var arr = jsArray.AsArray();
        var result = new JsValue[arr.Length];
        for (uint i = 0; i < arr.Length; i++)
        {
            result[i] = arr.Get(i);
        }
        return result;
    }
}

internal readonly record struct StepPolicyBinding(IResiliencePolicy? Policy, string? PolicyName);
