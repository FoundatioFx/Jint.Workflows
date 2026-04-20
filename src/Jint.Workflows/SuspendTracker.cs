using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foundatio.Resilience;
using Jint.Native;
using Jint.Runtime.Interop;

namespace Jint.Workflows;

/// <summary>
/// Tracks all awaited operations (steps and suspends) during a single workflow execution.
/// Steps and suspends share a single encounter counter to preserve ordering.
/// </summary>
internal sealed class WorkflowTracker
{
    private int _encounterIndex;
    private readonly List<JournalEntry> _journal;
    private readonly List<JsValue> _journalValues;
    private readonly Dictionary<string, Func<object?[], object?>> _stepImplementations;
    private readonly Dictionary<string, Func<object?[], CancellationToken, Task<object?>>> _asyncStepImplementations;
    private readonly Dictionary<string, Func<object?[], DateTimeOffset?>?> _suspendCallbacks;
    private readonly Dictionary<string, StepPolicyBinding> _stepPolicies;
    private readonly IResiliencePolicyProvider? _policyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _cancellationToken;

    public List<JournalEntry> NewJournal { get; }

    public bool IsSuspended { get; private set; }
    public SuspensionInfo? CurrentSuspension { get; private set; }

    public bool IsReplaying => _encounterIndex < _journal.Count;

    public WorkflowTracker(
        List<JournalEntry> journal,
        List<JsValue> journalValues,
        Dictionary<string, Func<object?[], object?>> stepImplementations,
        Dictionary<string, Func<object?[], CancellationToken, Task<object?>>> asyncStepImplementations,
        Dictionary<string, Func<object?[], DateTimeOffset?>?> suspendCallbacks,
        Dictionary<string, StepPolicyBinding> stepPolicies,
        IResiliencePolicyProvider? policyProvider,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        _journal = journal;
        _journalValues = journalValues;
        _stepImplementations = stepImplementations;
        _asyncStepImplementations = asyncStepImplementations;
        _suspendCallbacks = suspendCallbacks;
        _stepPolicies = stepPolicies;
        _policyProvider = policyProvider;
        _timeProvider = timeProvider;
        _cancellationToken = cancellationToken;
        NewJournal = new List<JournalEntry>(journal);
    }

    private IResiliencePolicy? ResolvePolicy(string name)
    {
        if (!_stepPolicies.TryGetValue(name, out var binding)) return null;
        if (binding.Policy is not null) return binding.Policy;
        if (binding.PolicyName is not null && _policyProvider is not null)
            return _policyProvider.GetPolicy(binding.PolicyName);
        return null;
    }

    public ClrFunction CreateSuspendFunction(Engine engine, string name)
    {
        return new ClrFunction(engine, name, (_, args) => HandleSuspend(engine, name, args));
    }

    public ClrFunction CreateStepFunction(Engine engine, string name)
    {
        return new ClrFunction(engine, name, (_, args) => HandleStep(engine, name, args));
    }

    private JsValue HandleSuspend(Engine engine, string name, JsValue[] args)
    {
        var index = _encounterIndex++;

        if (index < _journal.Count)
        {
            return _journalValues[index];
        }

        var clrArgs = ConvertArgsToClr(args);
        DateTimeOffset? resumeAt = null;

        if (_suspendCallbacks.TryGetValue(name, out var callback) && callback != null)
        {
            resumeAt = callback(clrArgs);
        }

        // First-suspension-wins: if another suspend (or retryable step) already
        // captured the suspension in this execution, don't overwrite it.
        // Subsequent pending promises stay pending; the later suspends re-run
        // on the next resume.
        if (!IsSuspended)
        {
            IsSuspended = true;
            CurrentSuspension = new SuspensionInfo(name, clrArgs, resumeAt);
        }

        var manualPromise = engine.Advanced.RegisterPromise();
        return manualPromise.Promise;
    }

    private JsValue HandleStep(Engine engine, string name, JsValue[] args)
    {
        var index = _encounterIndex++;

        if (index < _journal.Count)
        {
            var entry = _journal[index];
            if (string.Equals(entry.Type, "step_error", StringComparison.Ordinal))
            {
                var errorMsg = JsonSerializer.Serialize(entry.ResultJson ?? "Step failed");
                engine.Execute($"throw new Error({errorMsg})");
            }
            return _journalValues[index];
        }

        var clrArgs = ConvertArgsToClr(args);

        // Try sync implementation first, then async
        if (_stepImplementations.TryGetValue(name, out var syncImpl))
        {
            return ExecuteSyncStep(engine, name, clrArgs, syncImpl);
        }

        if (_asyncStepImplementations.TryGetValue(name, out var asyncImpl))
        {
            return ExecuteAsyncStep(engine, name, clrArgs, asyncImpl);
        }

        throw new InvalidOperationException($"Step function '{name}' has no registered implementation.");
    }

    private JsValue ExecuteSyncStep(Engine engine, string name, object?[] clrArgs, Func<object?[], object?> impl)
    {
        var policy = ResolvePolicy(name);
        object? clrResult;
        try
        {
            if (policy is not null)
            {
                clrResult = policy.ExecuteAsync<object?>(
                    _ => new ValueTask<object?>(impl(clrArgs)),
                    _cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                clrResult = impl(clrArgs);
            }
        }
        catch (AggregateException aex) when (aex.InnerException is RetryableStepException rex)
        {
            return HandleRetryable(engine, name, clrArgs, rex);
        }
        catch (RetryableStepException rex)
        {
            return HandleRetryable(engine, name, clrArgs, rex);
        }
        catch (AggregateException aex) when (aex.InnerException is not null)
        {
            return HandleStepError(engine, name, aex.InnerException);
        }
        catch (Exception ex)
        {
            return HandleStepError(engine, name, ex);
        }

        return RecordStepResult(engine, name, clrResult);
    }

    private JsValue ExecuteAsyncStep(Engine engine, string name, object?[] clrArgs, Func<object?[], CancellationToken, Task<object?>> impl)
    {
        var policy = ResolvePolicy(name);
        object? clrResult;
        try
        {
            if (policy is not null)
            {
                clrResult = policy.ExecuteAsync<object?>(
                    async ct => await impl(clrArgs, ct).ConfigureAwait(false),
                    _cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                // Block on the async result. The script is already synchronous at this point
                // (we're inside a ClrFunction call). The CancellationToken flows through.
                clrResult = impl(clrArgs, _cancellationToken).GetAwaiter().GetResult();
            }
        }
        catch (AggregateException aex) when (aex.InnerException is RetryableStepException rex)
        {
            return HandleRetryable(engine, name, clrArgs, rex);
        }
        catch (RetryableStepException rex)
        {
            return HandleRetryable(engine, name, clrArgs, rex);
        }
        catch (AggregateException aex) when (aex.InnerException is not null)
        {
            return HandleStepError(engine, name, aex.InnerException);
        }
        catch (Exception ex)
        {
            return HandleStepError(engine, name, ex);
        }

        return RecordStepResult(engine, name, clrResult);
    }

    private JsValue HandleRetryable(Engine engine, string name, object?[] clrArgs, RetryableStepException rex)
    {
        if (!IsSuspended)
        {
            IsSuspended = true;
            var resumeAt = _timeProvider.GetUtcNow().Add(rex.RetryAfter);
            CurrentSuspension = new SuspensionInfo(name, clrArgs, resumeAt, isRetry: true);
        }

        var manualPromise = engine.Advanced.RegisterPromise();
        return manualPromise.Promise;
    }

    private JsValue HandleStepError(Engine engine, string name, Exception ex)
    {
        NewJournal.Add(new JournalEntry("step_error", name, ex.Message));
        var errorMsg = JsonSerializer.Serialize(ex.Message);
        engine.Execute($"throw new Error({errorMsg})");
        return JsValue.Undefined; // unreachable
    }

    private JsValue RecordStepResult(Engine engine, string name, object? clrResult)
    {
        var jsResult = JsValue.FromObject(engine, clrResult);
        var resultJson = WorkflowEngine.JsValueToJson(engine, jsResult);
        NewJournal.Add(new JournalEntry("step", name, resultJson));
        return jsResult;
    }


    private static object?[] ConvertArgsToClr(JsValue[] args)
    {
        var result = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            result[i] = args[i].ToObject();
        }
        return result;
    }
}
