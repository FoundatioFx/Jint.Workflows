using Jint.Native;

namespace Jint.Workflows;

/// <summary>
/// The result of running or resuming a workflow.
/// </summary>
public sealed class WorkflowResult
{
    private WorkflowResult(WorkflowStatus status)
    {
        Status = status;
    }

    public WorkflowStatus Status { get; }

    /// <summary>
    /// The serializable workflow state. Non-null when <see cref="Status"/> is <see cref="WorkflowStatus.Suspended"/>.
    /// </summary>
    public WorkflowState? State { get; private init; }

    /// <summary>
    /// Information about which suspend function caused the suspension.
    /// Non-null when <see cref="Status"/> is <see cref="WorkflowStatus.Suspended"/>.
    /// </summary>
    public SuspensionInfo? Suspension { get; private init; }

    /// <summary>
    /// The final return value of the workflow function.
    /// Non-null when <see cref="Status"/> is <see cref="WorkflowStatus.Completed"/>.
    /// </summary>
    public JsValue? Value { get; private init; }

    /// <summary>
    /// The exception that caused the workflow to fault.
    /// Non-null when <see cref="Status"/> is <see cref="WorkflowStatus.Faulted"/>.
    /// </summary>
    public Exception? Exception { get; private init; }

    internal static WorkflowResult Suspended(WorkflowState state, SuspensionInfo suspension) => new(WorkflowStatus.Suspended)
    {
        State = state,
        Suspension = suspension,
    };

    internal static WorkflowResult Completed(JsValue value) => new(WorkflowStatus.Completed)
    {
        Value = value,
    };

    internal static WorkflowResult Faulted(Exception exception) => new(WorkflowStatus.Faulted)
    {
        Exception = exception,
    };

    internal static WorkflowResult ContinuedAsNew(WorkflowState state) => new(WorkflowStatus.ContinuedAsNew)
    {
        State = state,
    };
}

public enum WorkflowStatus
{
    Suspended,
    Completed,
    Faulted,
    ContinuedAsNew,
}

/// <summary>
/// Exception thrown when a workflow's JavaScript code throws an unhandled error.
/// </summary>
public sealed class WorkflowFaultedException : Exception
{
    public WorkflowFaultedException(JsValue error)
        : base(error.ToString())
    {
        Error = error;
    }

    /// <summary>
    /// The JavaScript error value that was thrown.
    /// </summary>
    public JsValue Error { get; }
}

/// <summary>
/// Thrown when a workflow is resumed with a script whose awaited operation
/// sequence no longer matches the journal. See <c>docs/versioning.md</c>
/// for safe vs. unsafe script edits.
/// </summary>
public sealed class JournalCompatibilityException : Exception
{
    public JournalCompatibilityException(int slotIndex, string expectedType, string expectedName, string encounteredType, string encounteredName)
        : base(FormatMessage(slotIndex, expectedType, expectedName, encounteredType, encounteredName))
    {
        SlotIndex = slotIndex;
        ExpectedType = expectedType;
        ExpectedName = expectedName;
        EncounteredType = encounteredType;
        EncounteredName = encounteredName;
    }

    public int SlotIndex { get; }
    public string ExpectedType { get; }
    public string ExpectedName { get; }
    public string EncounteredType { get; }
    public string EncounteredName { get; }

    private static string FormatMessage(int slotIndex, string expectedType, string expectedName, string encounteredType, string encounteredName)
    {
        var expected = string.IsNullOrEmpty(expectedName) ? expectedType : $"{expectedType}:{expectedName}";
        var encountered = string.IsNullOrEmpty(encounteredName) ? encounteredType : $"{encounteredType}:{encounteredName}";
        return $"Workflow journal is incompatible with the script at slot {slotIndex}. " +
               $"Expected {expected} (from journal) but script scheduled {encountered}. " +
               "The script was likely modified in a way that changes the sequence of awaited operations. " +
               "See docs/versioning.md for safe script edits.";
    }
}

/// <summary>
/// Throw from a step function implementation to signal a transient failure.
/// Instead of recording a permanent failure in the journal, the workflow
/// suspends with <see cref="SuspensionInfo.ResumeAt"/> set to the retry time.
/// The orchestrator should resume the workflow after the delay, and the step
/// will be re-executed (it is NOT journaled on retry).
/// </summary>
public class RetryableStepException : Exception
{
    public RetryableStepException(string message, TimeSpan retryAfter)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// How long to wait before retrying.
    /// </summary>
    public TimeSpan RetryAfter { get; }
}
