using System.Net.Http;
using Foundatio.Resilience;

namespace Jint.Workflows.Fetch;

/// <summary>
/// Configures the built-in <c>fetch</c> registration. Call <see cref="UseHttpClient"/>
/// exactly once, and optionally supply a default resilience policy and/or timeout.
/// <para>
/// Callers using <c>IHttpClientFactory</c> should resolve the client from the factory
/// themselves (e.g. <c>factory.CreateClient("jint-workflows")</c>) and pass the result.
/// </para>
/// </summary>
public sealed class FetchBuilder
{
    private HttpClient? _client;

    internal IResiliencePolicy? DefaultPolicy { get; private set; }
    internal TimeSpan? DefaultTimeout { get; private set; }

    /// <summary>
    /// Use a caller-owned <see cref="HttpClient"/> for all fetch requests.
    /// The workflow engine does not dispose the client.
    /// </summary>
    public FetchBuilder UseHttpClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        return this;
    }

    /// <summary>
    /// Apply a Foundatio resilience policy to every fetch request.
    /// Wraps the underlying HTTP call in <c>policy.ExecuteAsync</c>.
    /// </summary>
    public FetchBuilder UseDefaultPolicy(IResiliencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DefaultPolicy = policy;
        return this;
    }

    /// <summary>
    /// Apply a per-request timeout. Cancels the request after the elapsed time.
    /// </summary>
    public FetchBuilder UseDefaultTimeout(TimeSpan timeout)
    {
        DefaultTimeout = timeout;
        return this;
    }

    internal HttpClient ResolveClient()
    {
        return _client ?? throw new InvalidOperationException(
            "FetchBuilder requires a call to UseHttpClient(...).");
    }
}
