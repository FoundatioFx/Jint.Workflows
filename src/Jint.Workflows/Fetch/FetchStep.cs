using System.Net.Http;
using System.Text;

namespace Jint.Workflows.Fetch;

internal static class FetchStep
{
    private const string DefaultContentType = "application/json";

    public static async Task<object?> ExecuteAsync(
        FetchBuilder builder,
        object?[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is not string url)
        {
            throw new ArgumentException("fetch requires a URL string as the first argument.");
        }

        var init = args.Length > 1 ? args[1] as IDictionary<string, object?> : null;

        var method = GetString(init, "method") ?? "GET";
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        var reqHeaders = ExtractHeaders(init);
        var contentType = reqHeaders.TryGetValue("content-type", out var ct) ? ct : DefaultContentType;

        var bodyValue = TryGet(init, "body");
        if (bodyValue is not null)
        {
            var bodyString = bodyValue as string ?? bodyValue.ToString() ?? "";
            request.Content = new StringContent(bodyString, Encoding.UTF8, contentType);
        }

        foreach (var (k, v) in reqHeaders)
        {
            if (string.Equals(k, "content-type", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(k, v))
            {
                request.Content?.Headers.TryAddWithoutValidation(k, v);
            }
        }

        var client = builder.ResolveClient();

        using var linkedCts = builder.DefaultTimeout is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (linkedCts is not null && builder.DefaultTimeout is { } timeout)
        {
            linkedCts.CancelAfter(timeout);
        }
        var effectiveCt = linkedCts?.Token ?? cancellationToken;

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, effectiveCt)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(effectiveCt).ConfigureAwait(false);

        var respHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
        {
            respHeaders[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
        }
        if (response.Content is not null)
        {
            foreach (var h in response.Content.Headers)
            {
                respHeaders[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            }
        }

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var redirected = !string.Equals(finalUrl, url, StringComparison.Ordinal);
        var status = (int)response.StatusCode;

        return new Dictionary<string, object?>
        {
            ["ok"] = status >= 200 && status < 300,
            ["status"] = status,
            ["statusText"] = response.ReasonPhrase ?? "",
            ["url"] = finalUrl,
            ["redirected"] = redirected,
            ["headers"] = respHeaders,
            ["bodyText"] = body,
        };
    }

    private static Dictionary<string, string> ExtractHeaders(IDictionary<string, object?>? init)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (init is null || !init.TryGetValue("headers", out var h) || h is null) return result;

        if (h is IDictionary<string, object?> dict)
        {
            foreach (var kv in dict)
            {
                if (kv.Value is not null)
                {
                    result[kv.Key] = kv.Value.ToString() ?? "";
                }
            }
        }
        return result;
    }

    private static string? GetString(IDictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var v)) return null;
        return v?.ToString();
    }

    private static object? TryGet(IDictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var v)) return null;
        return v;
    }
}
