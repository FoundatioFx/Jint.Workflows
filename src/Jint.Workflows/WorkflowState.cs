using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jint.Workflows;

/// <summary>
/// A single entry in the workflow's replay journal. Records the result of
/// a completed step or suspend operation.
/// </summary>
public sealed class JournalEntry
{
    [JsonConstructor]
    public JournalEntry(string type, string name, string? resultJson)
    {
        Type = type;
        Name = name;
        ResultJson = resultJson;
    }

    /// <summary>
    /// The kind of operation: "step", "step_error", or "suspend".
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// The registered function name (e.g. "fetchOrder", "sleep").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// JSON-serialized result value, or null for undefined.
    /// </summary>
    public string? ResultJson { get; }
}

/// <summary>
/// Serializable state of a suspended workflow. Contains the replay journal,
/// run metadata, and arguments — but not the script. The caller provides
/// the script at both start and resume time, allowing scripts to be updated
/// between suspensions as long as the journal remains compatible.
/// </summary>
public sealed class WorkflowState
{
    /// <summary>
    /// Current serialization format version.
    /// </summary>
    public const int CurrentVersion = 2;

    [JsonConstructor]
    public WorkflowState(
        string entryPoint,
        string argumentsJson,
        List<JournalEntry> journal,
        string runId,
        long startedAtMs,
        int version = CurrentVersion,
        Dictionary<string, string>? metadata = null)
    {
        EntryPoint = entryPoint;
        ArgumentsJson = argumentsJson;
        Journal = journal;
        RunId = runId;
        StartedAtMs = startedAtMs;
        Version = version;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    public int Version { get; }
    public string EntryPoint { get; }
    public string ArgumentsJson { get; }
    public List<JournalEntry> Journal { get; }
    public string RunId { get; }
    public long StartedAtMs { get; }

    /// <summary>
    /// Arbitrary key-value metadata attached to the workflow state.
    /// Use this for orchestrator concerns like tags, tenant ID, priority, etc.
    /// Persisted across serialization/deserialization but ignored by the engine.
    /// </summary>
    public Dictionary<string, string> Metadata { get; }

    /// <summary>
    /// When true, the last suspension was a step retry. On resume, the step
    /// will re-execute instead of adding a suspend journal entry.
    /// Not serialized — only meaningful between suspend and immediate resume.
    /// </summary>
    [JsonIgnore]
    public bool IsRetry { get; internal set; }

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, WorkflowJsonContext.Default.WorkflowState);
    }

    public static WorkflowState Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("version", out var versionEl))
        {
            var version = versionEl.GetInt32();
            if (version > CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"WorkflowState version {version} is newer than supported version {CurrentVersion}. Upgrade Jint.Workflows to resume this workflow.");
            }
        }

        return JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.WorkflowState)
               ?? throw new JsonException("Failed to deserialize WorkflowState.");
    }
}

[JsonSerializable(typeof(WorkflowState))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class WorkflowJsonContext : JsonSerializerContext;
