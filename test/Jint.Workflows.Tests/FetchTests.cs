using System.Net;
using System.Net.Http;
using System.Text;
using Foundatio.Resilience;
using Jint.Workflows;
using Jint.Workflows.Fetch;

namespace Jint.Workflows.Tests;

public class FetchTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public int CallCount;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            // Preserve the request for inspection. Clone content since the body stream is disposed after send.
            string? bodyCopy = null;
            if (request.Content is not null)
            {
                bodyCopy = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = bodyCopy is null ? null : new StringContent(bodyCopy, Encoding.UTF8, request.Content!.Headers.ContentType?.MediaType ?? "text/plain"),
            });
            foreach (var h in request.Headers)
            {
                Requests[^1].Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return await _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
    {
        var r = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return r;
    }

    [Fact]
    public void Fetch_Get200_ParsesJsonBody()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"name\":\"alice\",\"age\":30}")));
        var http = new HttpClient(handler);
        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http));

        var result = workflow.RunWorkflow(@"
            async function main() {
                const res = await fetch('https://example.com/user');
                if (!res.ok) throw new Error('bad');
                const user = await res.json();
                return user.name + ':' + user.age;
            }
        ", "main");

        if (result.Status == WorkflowStatus.Faulted)
            throw new Exception($"Faulted: {result.Exception?.Message}", result.Exception);

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal("alice:30", result.Value!.AsString());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Fetch_PostWithJsonBody_SendsBodyAndHeaders()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.Created, "{\"id\":42}")));
        var http = new HttpClient(handler);
        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http));

        var result = workflow.RunWorkflow(@"
            async function main() {
                const res = await fetch('https://example.com/orders', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'X-Token': 'abc' },
                    body: JSON.stringify({ sku: 'WIDGET' })
                });
                const data = await res.json();
                return res.status + ':' + data.id;
            }
        ", "main");

        Assert.Equal("201:42", result.Value!.AsString());
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.True(handler.Requests[0].Headers.Contains("X-Token"));
    }

    [Fact]
    public void Fetch_NonSuccessStatus_DoesNotThrow()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("oops", Encoding.UTF8, "text/plain"),
            ReasonPhrase = "Internal Server Error",
        }));
        var http = new HttpClient(handler);
        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http));

        var result = workflow.RunWorkflow(@"
            async function main() {
                const res = await fetch('https://example.com/x');
                return res.ok + ':' + res.status + ':' + res.statusText + ':' + await res.text();
            }
        ", "main");

        Assert.Equal("false:500:Internal Server Error:oops", result.Value!.AsString());
    }

    [Fact]
    public void Fetch_WithPolicy_RetriesTransientFailures()
    {
        int seq = 0;
        var handler = new StubHandler((_, _) =>
        {
            seq++;
            if (seq < 3) throw new HttpRequestException("transient");
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "\"recovered\""));
        });
        var http = new HttpClient(handler);

        var policy = new ResiliencePolicyBuilder()
            .WithMaxAttempts(5)
            .WithDelay(TimeSpan.Zero)
            .Build();

        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http).UseDefaultPolicy(policy));

        var result = workflow.RunWorkflow(@"
            async function main() {
                const res = await fetch('https://example.com/flaky');
                return await res.json();
            }
        ", "main");

        Assert.Equal("recovered", result.Value!.AsString());
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public void Fetch_ReplayReturnsCachedResponse_NoHttpCall()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"v\":1}")));
        var http = new HttpClient(handler);
        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http));
        workflow.RegisterSuspendFunction("wait");

        var script = @"
            async function main() {
                const res = await fetch('https://example.com/x');
                const data = await res.json();
                await wait();
                return data.v;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.Equal(1, handler.CallCount);

        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(1.0, r2.Value!.AsNumber());
        Assert.Equal(1, handler.CallCount); // no second HTTP call on replay
    }

    [Fact]
    public void Fetch_ResponseClone_IndependentBodyUsed()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"x\":42}")));
        var http = new HttpClient(handler);
        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http));

        var result = workflow.RunWorkflow(@"
            async function main() {
                const res = await fetch('https://example.com/x');
                const copy = res.clone();
                const a = await res.json();
                const b = await copy.json();
                return a.x + ':' + b.x;
            }
        ", "main");

        Assert.Equal("42:42", result.Value!.AsString());
    }

    [Fact]
    public void Fetch_NotEnabled_ScriptFails()
    {
        var workflow = new WorkflowEngine();

        var result = workflow.RunWorkflow(@"
            async function main() {
                try { await fetch('https://example.com/x'); return 'no throw'; }
                catch (e) { return 'caught'; }
            }
        ", "main");

        Assert.Equal("caught", result.Value!.AsString());
    }

    [Fact]
    public void Fetch_HeadersCaseInsensitive()
    {
        var handler = new StubHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("body", Encoding.UTF8, "text/plain"),
            };
            r.Headers.Add("X-Custom", "hello");
            return Task.FromResult(r);
        });
        var http = new HttpClient(handler);
        var workflow = new WorkflowEngine()
            .EnableFetch(b => b.UseHttpClient(http));

        var result = workflow.RunWorkflow(@"
            async function main() {
                const res = await fetch('https://example.com/x');
                return res.headers['x-custom'] + '|' + res.headers['content-type'];
            }
        ", "main");

        Assert.StartsWith("hello|text/plain", result.Value!.AsString());
    }
}
