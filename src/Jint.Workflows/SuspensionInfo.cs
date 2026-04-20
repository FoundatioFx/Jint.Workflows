namespace Jint.Workflows;

/// <summary>
/// Describes why a workflow was suspended — the function name that caused
/// suspension and the arguments it was called with.
/// </summary>
public sealed class SuspensionInfo
{
    public SuspensionInfo(
        string functionName,
        object?[] arguments,
        DateTimeOffset? resumeAt = null,
        bool isRetry = false,
        IReadOnlyList<string>? eventNames = null)
    {
        FunctionName = functionName;
        Arguments = arguments;
        ResumeAt = resumeAt;
        IsRetry = isRetry;
        EventNames = eventNames;
    }

    /// <summary>
    /// The name of the suspend function that was called (e.g. "sleep", "getApproval").
    /// </summary>
    public string FunctionName { get; }

    /// <summary>
    /// The arguments passed to the suspend function, converted to CLR types.
    /// </summary>
    public object?[] Arguments { get; }

    /// <summary>
    /// For time-based suspensions (e.g. built-in <c>sleep()</c>), the computed
    /// UTC time at which the workflow should be resumed. Null for non-time-based suspensions.
    /// </summary>
    public DateTimeOffset? ResumeAt { get; }

    /// <summary>
    /// True when this suspension is a step retry (from <see cref="RetryableStepException"/>).
    /// When resuming a retry, no journal entry should be added — the step will re-execute.
    /// </summary>
    public bool IsRetry { get; }

    /// <summary>
    /// For <c>waitForEvent(...)</c> suspensions, the event names the workflow is waiting for.
    /// Null for other kinds of suspensions.
    /// </summary>
    public IReadOnlyList<string>? EventNames { get; }
}
