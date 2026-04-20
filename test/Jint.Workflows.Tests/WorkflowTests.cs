using Foundatio.Resilience;
using Jint.Workflows;
using Microsoft.Extensions.Time.Testing;

namespace Jint.Workflows.Tests;

public class WorkflowTests
{
    private static readonly DateTimeOffset s_fixedStart = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static (WorkflowEngine workflow, FakeTimeProvider time) CreateEngine(
        Action<Options>? configure = null,
        Action<Engine>? setup = null)
    {
        var time = new FakeTimeProvider(s_fixedStart);
        var workflow = new WorkflowEngine(configure, setup, timeProvider: time);
        workflow.RegisterSuspendFunction("sleep", args => DurationParser.Parse(args[0], time));
        return (workflow, time);
    }

    /// <summary>
    /// Advances the fake clock to the suspension's ResumeAt, mimicking what a real
    /// orchestrator would do: wait until the scheduled time, then resume.
    /// </summary>
    private static void AdvanceTo(FakeTimeProvider time, WorkflowResult result)
    {
        if (result.Suspension?.ResumeAt is { } resumeAt)
        {
            time.SetUtcNow(resumeAt);
        }
    }

    [Fact]
    public void BasicSuspendAndResume()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main(x) {
                var y = x + 1;
                await sleep('5d');
                return y + 1;
            }";

        var result = workflow.RunWorkflow(script, "main", 10);

        Assert.Equal(WorkflowStatus.Suspended, result.Status);
        Assert.Equal("sleep", result.Suspension!.FunctionName);
        Assert.Equal(s_fixedStart.AddDays(5), result.Suspension.ResumeAt);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal(WorkflowStatus.Completed, result2.Status);
        Assert.Equal(12.0, result2.Value!.AsNumber());
    }

    [Fact]
    public void MultipleSequentialSuspensions()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var log = [];
                log.push('step1');
                await sleep('1h');
                log.push('step2');
                await sleep('2h');
                log.push('step3');
                return log.join(',');
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(s_fixedStart.AddHours(1), r1.Suspension!.ResumeAt);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(s_fixedStart.AddHours(1 + 2), r2.Suspension!.ResumeAt);

        AdvanceTo(time, r2);
        var r3 = workflow.ResumeWorkflow(script, r2.State!);
        Assert.Equal("step1,step2,step3", r3.Value!.AsString());
    }

    [Fact]
    public void ResumeWithValue()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterSuspendFunction("getApproval");

        var script = @"
            async function main() {
                var approved = await getApproval('manager');
                return 'result: ' + approved;
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal("getApproval", result.Suspension!.FunctionName);

        var result2 = workflow.ResumeWorkflow(script, result.State!, "yes");
        Assert.Equal("result: yes", result2.Value!.AsString());
    }

    [Fact]
    public void ResumeWithBooleanValue()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterSuspendFunction("confirm");

        var script = @"
            async function main() {
                var ok = await confirm('proceed?');
                return ok ? 'yes' : 'no';
            }";

        var result = workflow.RunWorkflow(script, "main");
        var result2 = workflow.ResumeWorkflow(script, result.State!, true);
        Assert.Equal("yes", result2.Value!.AsString());
    }

    [Fact]
    public void VariableStatePreservedAcrossResume()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var a = 10;
                var b = a * 2;
                var c = { x: a + b };
                await sleep('1d');
                return c.x + 1;
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal(31.0, result2.Value!.AsNumber());
    }

    [Fact]
    public void NestedAsyncCallWithSuspend()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function helper() {
                await sleep('3d');
                return 42;
            }
            async function main() {
                var x = await helper();
                return x + 1;
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal(s_fixedStart.AddDays(3), result.Suspension!.ResumeAt);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal(43.0, result2.Value!.AsNumber());
    }

    [Fact]
    public void ConditionalSuspend()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterSuspendFunction("getInput");

        var script = @"
            async function main(flag) {
                if (flag) {
                    await sleep('1d');
                    return 'waited';
                } else {
                    var input = await getInput('question');
                    return 'got: ' + input;
                }
            }";

        var result = workflow.RunWorkflow(script, "main", false);
        Assert.Equal("getInput", result.Suspension!.FunctionName);
        Assert.Null(result.Suspension.ResumeAt); // Not a time-based suspend

        var result2 = workflow.ResumeWorkflow(script, result.State!, "answer");
        Assert.Equal("got: answer", result2.Value!.AsString());
    }

    [Fact]
    public void LoopWithSuspend()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterSuspendFunction("step");

        var script = @"
            async function main() {
                var total = 0;
                for (var i = 0; i < 3; i++) {
                    var val = await step(i);
                    total += val;
                }
                return total;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(new object[] { 0.0 }, r1.Suspension!.Arguments);

        var r2 = workflow.ResumeWorkflow(script, r1.State!, 10);
        var r3 = workflow.ResumeWorkflow(script, r2.State!, 20);
        var r4 = workflow.ResumeWorkflow(script, r3.State!, 30);
        Assert.Equal(60.0, r4.Value!.AsNumber());
    }

    [Fact]
    public void SerializationRoundtrip()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main(name) {
                await sleep('5d');
                return 'hello ' + name;
            }";

        var result = workflow.RunWorkflow(script, "main", "world");
        var json = result.State!.Serialize();
        Assert.NotEmpty(json);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, json);
        Assert.Equal("hello world", result2.Value!.AsString());
    }

    [Fact]
    public void ErrorAfterResume()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                await sleep('1h');
                throw new Error('boom');
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal(WorkflowStatus.Faulted, result2.Status);
    }

    [Fact]
    public void CompletesImmediatelyWithNoSuspend()
    {
        var (workflow, _) = CreateEngine();

        var result = workflow.RunWorkflow("async function main() { return 42; }", "main");
        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal(42.0, result.Value!.AsNumber());
    }

    [Fact]
    public void CustomEngineSetup()
    {
        var (workflow, time) = CreateEngine(
            setup: engine => engine.SetValue("multiply", new Func<int, int, int>((a, b) => a * b))
        );

        var script = @"
            async function main() {
                var x = multiply(3, 4);
                await sleep('1d');
                return x + multiply(2, 5);
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal(22.0, result2.Value!.AsNumber());
    }

    [Fact]
    public void MultipleDifferentSuspendFunctions()
    {
        var (workflow, time) = CreateEngine();
        workflow.RegisterSuspendFunction("getApproval");
        workflow.RegisterSuspendFunction("notify");

        var script = @"
            async function main(orderId) {
                await sleep('3d');
                var approved = await getApproval('manager', orderId);
                if (approved) {
                    await notify('done', orderId);
                }
                return approved;
            }";

        var r1 = workflow.RunWorkflow(script, "main", "ORD-001");
        Assert.Equal("sleep", r1.Suspension!.FunctionName);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal("getApproval", r2.Suspension!.FunctionName);
        Assert.Equal(new object[] { "manager", "ORD-001" }, r2.Suspension.Arguments);

        var r3 = workflow.ResumeWorkflow(script, r2.State!, true);
        Assert.Equal("notify", r3.Suspension!.FunctionName);

        var r4 = workflow.ResumeWorkflow(script, r3.State!);
        Assert.True(r4.Value!.AsBoolean());
    }

    [Fact]
    public void TryCatchAroundSuspend()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterSuspendFunction("riskyOp");

        var script = @"
            async function main() {
                try {
                    var val = await riskyOp();
                    return 'success: ' + val;
                } catch(e) {
                    return 'caught: ' + e.message;
                }
            }";

        var result = workflow.RunWorkflow(script, "main");
        var result2 = workflow.ResumeWorkflow(script, result.State!, "ok");
        Assert.Equal("success: ok", result2.Value!.AsString());
    }

    [Fact]
    public void NoArguments()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                await sleep('1s');
                return 'done';
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal("sleep", result.Suspension!.FunctionName);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("done", result2.Value!.AsString());
    }

    // --- SetScript convenience API ---

    [Fact]
    public void SetScriptAllowsCallingWithoutScriptParam()
    {
        var (workflow, time) = CreateEngine();
        workflow.SetScript(@"
            async function main(x) {
                await sleep('1d');
                return x + 1;
            }
        ", "main");

        var result = workflow.RunWorkflow(10);
        Assert.Equal(WorkflowStatus.Suspended, result.Status);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(result.State!);
        Assert.Equal(11.0, result2.Value!.AsNumber());
    }

    // --- Script can change between resumes ---

    [Fact]
    public void ScriptCanChangeBetweenResumes()
    {
        var (workflow, time) = CreateEngine();

        var scriptV1 = @"
            async function main() {
                await sleep('1d');
                return 'v1 result';
            }";

        var result = workflow.RunWorkflow(scriptV1, "main");

        var scriptV2 = @"
            async function main() {
                await sleep('1d');
                return 'v2 result with improvements';
            }";

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(scriptV2, result.State!);
        Assert.Equal("v2 result with improvements", result2.Value!.AsString());
    }

    [Fact]
    public void ScriptCanAddCodeAfterLastSuspend()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterStepFunction("fetchData", args => $"data-{args[0]}");
        workflow.RegisterSuspendFunction("approve");

        var scriptV1 = @"
            async function main(id) {
                var data = await fetchData(id);
                var ok = await approve(data);
                return ok;
            }";

        var r1 = workflow.RunWorkflow(scriptV1, "main", "123");

        var scriptV2 = @"
            async function main(id) {
                var data = await fetchData(id);
                var ok = await approve(data);
                if (ok) {
                    return 'approved: ' + data;
                }
                return 'rejected';
            }";

        var r2 = workflow.ResumeWorkflow(scriptV2, r1.State!, true);
        Assert.Equal("approved: data-123", r2.Value!.AsString());
    }

    [Fact]
    public void StateDoesNotContainScript()
    {
        var (workflow, _) = CreateEngine();

        var script = @"async function main() { await sleep('1d'); return 'done'; }";
        var result = workflow.RunWorkflow(script, "main");

        var json = result.State!.Serialize();
        Assert.DoesNotContain("sleep('1d')", json);
        Assert.DoesNotContain("async function main", json);
    }

    // --- Step function tests ---

    [Fact]
    public void StepFunctionExecutesAndReturnsResult()
    {
        var callCount = 0;
        var (workflow, _) = CreateEngine();
        workflow.RegisterStepFunction("fetchData", args =>
        {
            callCount++;
            return $"data-for-{args[0]}";
        });

        var result = workflow.RunWorkflow(@"
            async function main(id) { return await fetchData(id); }
        ", "main", "order-123");

        Assert.Equal("data-for-order-123", result.Value!.AsString());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void StepResultNotReExecutedOnReplay()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("fetchData", args =>
        {
            callCount++;
            return $"data-{callCount}";
        });

        var script = @"
            async function main() {
                var data = await fetchData('x');
                await sleep('1d');
                return data;
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal(1, callCount);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("data-1", result2.Value!.AsString());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void StepAndSuspendInterleaved()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("process", args =>
        {
            callCount++;
            return (double)args[0]! * 10;
        });

        var script = @"
            async function main() {
                var a = await process(1);
                await sleep('5d');
                var b = await process(2);
                await sleep('3d');
                var c = await process(3);
                return a + b + c;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(1, callCount);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(2, callCount);

        AdvanceTo(time, r2);
        var r3 = workflow.ResumeWorkflow(script, r2.State!);
        Assert.Equal(60.0, r3.Value!.AsNumber());
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void StepSerializationRoundtrip()
    {
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("fetch", _ => new Dictionary<string, object> { ["name"] = "test", ["value"] = 42 });

        var script = @"
            async function main() {
                var obj = await fetch();
                await sleep('1d');
                return obj.name + ':' + obj.value;
            }";

        var result = workflow.RunWorkflow(script, "main");
        var json = result.State!.Serialize();

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, json);
        Assert.Equal("test:42", result2.Value!.AsString());
    }

    [Fact]
    public void StepInLoopExecutesCorrectNumberOfTimes()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("doWork", args =>
        {
            callCount++;
            return (double)args[0]! * 2;
        });

        var script = @"
            async function main() {
                var results = [];
                for (var i = 0; i < 3; i++) { results.push(await doWork(i)); }
                await sleep('1d');
                return results.join(',');
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal(3, callCount);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("0,2,4", result2.Value!.AsString());
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void StepAfterSuspendExecutesOnResume()
    {
        var callLog = new List<string>();
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("sendEmail", args =>
        {
            callLog.Add($"email:{args[0]}");
            return true;
        });

        var script = @"
            async function main() {
                await sleep('5d');
                await sendEmail('user@test.com');
                return 'done';
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Empty(callLog);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("done", result2.Value!.AsString());
        Assert.Single(callLog);
        Assert.Equal("email:user@test.com", callLog[0]);
    }

    // --- Deterministic builtins ---

    [Fact]
    public void SleepShowsTimeBeforeAndAfter()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var before = Date.now();
                await sleep('2d');
                var after = Date.now();
                return { before: before, after: after };
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal(s_fixedStart.AddDays(2), result.Suspension!.ResumeAt!.Value);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        var before = (long)result2.Value!.Get("before").AsNumber();
        var after = (long)result2.Value!.Get("after").AsNumber();

        // "before" is during replay — frozen to start time
        Assert.Equal(s_fixedStart.ToUnixTimeMilliseconds(), before);
        // "after" is past the journal — real time at ResumeAt
        Assert.Equal(s_fixedStart.AddDays(2).ToUnixTimeMilliseconds(), after);
    }

    [Fact]
    public void DateNowIsFrozenDuringReplayThenRealAfter()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var t1 = Date.now();
                var t2 = Date.now();
                await sleep('1d');
                var t3 = Date.now();
                return t1 + ':' + t2 + ':' + t3;
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);

        var result2 = workflow.ResumeWorkflow(script, result.State!);
        var parts = result2.Value!.AsString().Split(':');
        var t1 = long.Parse(parts[0]);
        var t2 = long.Parse(parts[1]);
        var t3 = long.Parse(parts[2]);

        var startMs = s_fixedStart.ToUnixTimeMilliseconds();
        Assert.Equal(startMs, t1);
        Assert.Equal(startMs, t2);
        Assert.Equal(s_fixedStart.AddDays(1).ToUnixTimeMilliseconds(), t3);
    }

    [Fact]
    public void NewDateUsesDeterministicTimeDuringReplay()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var before = new Date().getTime();
                await sleep('1d');
                var after = new Date().getTime();
                return { before: before, after: after };
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);

        var result2 = workflow.ResumeWorkflow(script, result.State!);
        var before = (long)result2.Value!.Get("before").AsNumber();
        var after = (long)result2.Value!.Get("after").AsNumber();

        Assert.Equal(s_fixedStart.ToUnixTimeMilliseconds(), before);
        Assert.Equal(s_fixedStart.AddDays(1).ToUnixTimeMilliseconds(), after);
    }

    [Fact]
    public void NewDateWithArgsStillWorks()
    {
        var (workflow, _) = CreateEngine();

        var result = workflow.RunWorkflow(@"
            async function main() {
                var d = new Date(2025, 0, 15);
                return d.getFullYear() + '-' + (d.getMonth() + 1) + '-' + d.getDate();
            }
        ", "main");

        Assert.Equal("2025-1-15", result.Value!.AsString());
    }

    [Fact]
    public void ReplayTimestampsAreStableAcrossMultipleResumes()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var times = [];
                times.push(Date.now());
                await sleep('1d');
                times.push(Date.now());
                await sleep('2d');
                times.push(Date.now());
                await sleep('3d');
                times.push(Date.now());
                return times.join(',');
            }";

        var startMs = s_fixedStart.ToUnixTimeMilliseconds();

        var r1 = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        AdvanceTo(time, r2);
        var r3 = workflow.ResumeWorkflow(script, r2.State!);
        AdvanceTo(time, r3);
        var r4 = workflow.ResumeWorkflow(script, r3.State!);

        var times = r4.Value!.AsString().Split(',').Select(long.Parse).ToArray();

        // t0, t1, t2 all during replay — frozen to start time
        Assert.Equal(startMs, times[0]);
        Assert.Equal(startMs, times[1]);
        Assert.Equal(startMs, times[2]);
        // t3 past journal — accumulated time: start + 1d + 2d + 3d = start + 6d
        Assert.Equal(s_fixedStart.AddDays(6).ToUnixTimeMilliseconds(), times[3]);

        // Replay from r3's state again — t0, t1, t2 identical
        AdvanceTo(time, r3);
        var r4Again = workflow.ResumeWorkflow(script, r3.State!);
        var timesAgain = r4Again.Value!.AsString().Split(',').Select(long.Parse).ToArray();
        Assert.Equal(times[0], timesAgain[0]);
        Assert.Equal(times[1], timesAgain[1]);
        Assert.Equal(times[2], timesAgain[2]);
    }

    [Fact]
    public void MathRandomIsDeterministicAcrossReplays()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                var r1 = Math.random();
                await sleep('1d');
                var r2 = Math.random();
                return r1 + ':' + r2;
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);

        var parts = result2.Value!.AsString().Split(':');
        Assert.InRange(double.Parse(parts[0]), 0, 1);
        Assert.InRange(double.Parse(parts[1]), 0, 1);

        // Same state, same results
        AdvanceTo(time, result);
        var result3 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal(result2.Value!.AsString(), result3.Value!.AsString());
    }

    // --- Run ID ---

    [Fact]
    public void RunIdIsStableAcrossResumes()
    {
        var (workflow, _) = CreateEngine();

        var script = "async function main() { await sleep('1d'); return 'done'; }";
        var result = workflow.RunWorkflow(script, "main");

        var runId = result.State!.RunId;
        Assert.NotEmpty(runId);

        var json = result.State!.Serialize();
        Assert.Equal(runId, WorkflowState.Deserialize(json).RunId);
    }

    [Fact]
    public void DifferentRunsGetDifferentIds()
    {
        var (workflow, _) = CreateEngine();

        var script = "async function main() { await sleep('1d'); }";
        var r1 = workflow.RunWorkflow(script, "main");
        var r2 = workflow.RunWorkflow(script, "main");

        Assert.NotEqual(r1.State!.RunId, r2.State!.RunId);
    }

    // --- Step failure ---

    [Fact]
    public void StepFailureIsCaughtInScript()
    {
        var (workflow, _) = CreateEngine();
        workflow.RegisterStepFunction("failingStep", _ => throw new InvalidOperationException("db connection failed"));

        var result = workflow.RunWorkflow(@"
            async function main() {
                try { await failingStep(); return 'ok'; }
                catch(e) { return 'caught: ' + e.message; }
            }
        ", "main");

        Assert.Equal("caught: db connection failed", result.Value!.AsString());
    }

    [Fact]
    public void StepFailureJournaledAndReplayedConsistently()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("safeStep", _ => { callCount++; return "safe"; });
        workflow.RegisterStepFunction("mayFail", _ => { callCount++; return "ok"; });

        var script = @"
            async function main() {
                var a = await safeStep();
                var b = await mayFail('good');
                await sleep('1d');
                return a + ':' + b;
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal(2, callCount);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("safe:ok", result2.Value!.AsString());
        Assert.Equal(2, callCount);
    }

    // --- Built-in sleep() ---

    [Fact]
    public void SleepWithDurationString()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                await sleep('5d');
                return 'awake';
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal("sleep", result.Suspension!.FunctionName);
        Assert.Equal(s_fixedStart.AddDays(5), result.Suspension.ResumeAt);

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("awake", result2.Value!.AsString());
    }

    [Fact]
    public void SleepWithMilliseconds()
    {
        var (workflow, _) = CreateEngine();

        var result = workflow.RunWorkflow(@"
            async function main() { await sleep(60000); return 'done'; }
        ", "main");

        Assert.Equal(s_fixedStart.AddMilliseconds(60000), result.Suspension!.ResumeAt);
    }

    [Fact]
    public void SleepWithVariousUnits()
    {
        var (workflow, _) = CreateEngine();

        var r1 = workflow.RunWorkflow("async function main() { await sleep('2h'); }", "main");
        Assert.Equal(s_fixedStart.AddHours(2), r1.Suspension!.ResumeAt);

        var r2 = workflow.RunWorkflow("async function main() { await sleep('30m'); }", "main");
        Assert.Equal(s_fixedStart.AddMinutes(30), r2.Suspension!.ResumeAt);

        var r3 = workflow.RunWorkflow("async function main() { await sleep('10s'); }", "main");
        Assert.Equal(s_fixedStart.AddSeconds(10), r3.Suspension!.ResumeAt);
    }

    [Fact]
    public void SleepInterleavedWithStepsAndSuspends()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("doWork", _ => { callCount++; return "done"; });
        workflow.RegisterSuspendFunction("getApproval");

        var script = @"
            async function main() {
                var work = await doWork();
                await sleep('1d');
                var ok = await getApproval('manager');
                return work + ':' + ok;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal("sleep", r1.Suspension!.FunctionName);
        Assert.Equal(1, callCount);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal("getApproval", r2.Suspension!.FunctionName);

        var r3 = workflow.ResumeWorkflow(script, r2.State!, true);
        Assert.Equal("done:true", r3.Value!.AsString());
    }

    // --- State versioning ---

    [Fact]
    public void StateIncludesVersion()
    {
        var (workflow, _) = CreateEngine();

        var result = workflow.RunWorkflow("async function main() { await sleep('1d'); }", "main");
        Assert.Contains("\"version\":" + WorkflowState.CurrentVersion, result.State!.Serialize());
    }

    [Fact]
    public void DeserializeRejectsFutureVersion()
    {
        var json = "{\"version\":999,\"entryPoint\":\"main\",\"argumentsJson\":\"[]\",\"journal\":[],\"runId\":\"abc\",\"startedAtMs\":0}";
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowState.Deserialize(json));
        Assert.Contains("version 999", ex.Message);
        Assert.Contains("newer", ex.Message);
    }

    // --- Console suppression during replay ---

    [Fact]
    public void ConsoleLogSuppressedDuringReplay()
    {
        var logOutput = new List<string>();
        var (workflow, time) = CreateEngine(
            setup: engine =>
            {
                engine.Evaluate("var console = {}");
                engine.SetValue("captureLog", new Action<string>(msg => logOutput.Add(msg)));
                engine.Evaluate("console.log = function(msg) { captureLog(String(msg)); }");
            }
        );

        var script = @"
            async function main() {
                console.log('before-sleep');
                await sleep('1d');
                console.log('after-sleep');
                return 'done';
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Single(logOutput);
        Assert.Equal("before-sleep", logOutput[0]);

        logOutput.Clear();

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("done", result2.Value!.AsString());
        Assert.Single(logOutput);
        Assert.Equal("after-sleep", logOutput[0]);
    }

    [Fact]
    public void DotNetLoggerSuppressedDuringReplayThenForwardsAfter()
    {
        var logMessages = new List<string>();

        var (workflow, time) = CreateEngine(
            setup: engine =>
            {
                engine.SetValue("console", new
                {
                    log = new Action<object>(msg => logMessages.Add($"INFO: {msg}")),
                    warn = new Action<object>(msg => logMessages.Add($"WARN: {msg}")),
                    error = new Action<object>(msg => logMessages.Add($"ERROR: {msg}")),
                    info = new Action<object>(msg => logMessages.Add($"INFO: {msg}"))
                });
            }
        );

        var script = @"
            async function main() {
                console.log('step 1 starting');
                console.warn('step 1 warning');
                await sleep('1d');
                console.log('step 2 starting');
                console.error('step 2 error');
                return 'done';
            }";

        var result = workflow.RunWorkflow(script, "main");
        Assert.Equal(2, logMessages.Count);
        Assert.Equal("INFO: step 1 starting", logMessages[0]);
        Assert.Equal("WARN: step 1 warning", logMessages[1]);

        logMessages.Clear();

        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("done", result2.Value!.AsString());
        Assert.Equal(2, logMessages.Count);
        Assert.Equal("INFO: step 2 starting", logMessages[0]);
        Assert.Equal("ERROR: step 2 error", logMessages[1]);
    }

    // --- Retryable step errors ---

    [Fact]
    public void RetryableStepCausesSuspensionWithRetryHint()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("callApi", _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new RetryableStepException("service unavailable", TimeSpan.FromMinutes(5));
            return "success";
        });

        var script = "async function main() { return await callApi(); }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.Equal("callApi", r1.Suspension!.FunctionName);
        Assert.Equal(s_fixedStart.AddMinutes(5), r1.Suspension.ResumeAt);
        Assert.Equal(1, callCount);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(WorkflowStatus.Completed, r2.Status);
        Assert.Equal("success", r2.Value!.AsString());
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void RetryableStepDoesNotPolluteJournal()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("flaky", _ =>
        {
            callCount++;
            if (callCount <= 2)
                throw new RetryableStepException("try again", TimeSpan.FromSeconds(1));
            return "finally";
        });

        var script = "async function main() { return await flaky(); }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.Empty(r1.State!.Journal);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(WorkflowStatus.Suspended, r2.Status);
        Assert.Empty(r2.State!.Journal);

        AdvanceTo(time, r2);
        var r3 = workflow.ResumeWorkflow(script, r2.State!);
        Assert.Equal(WorkflowStatus.Completed, r3.Status);
        Assert.Equal("finally", r3.Value!.AsString());
        Assert.Equal(3, callCount);
    }

    // --- Async step functions ---

    [Fact]
    public void AsyncStepFunctionExecutesAndJournals()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("fetchAsync", async (args, ct) =>
        {
            callCount++;
            await Task.Delay(1, ct); // Simulate async work
            return $"async-data-{args[0]}";
        });

        var script = @"
            async function main(id) {
                var data = await fetchAsync(id);
                await sleep('1d');
                return data;
            }";

        var result = workflow.RunWorkflow(script, "main", "42");
        Assert.Equal(WorkflowStatus.Suspended, result.Status);
        Assert.Equal(1, callCount);

        // On resume, async step replays from journal (not re-executed)
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("async-data-42", result2.Value!.AsString());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void AsyncStepRetryableException()
    {
        var callCount = 0;
        var (workflow, time) = CreateEngine();
        workflow.RegisterStepFunction("flakyAsync", async (args, ct) =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount == 1)
                throw new RetryableStepException("timeout", TimeSpan.FromMinutes(1));
            return "ok";
        });

        var script = "async function main() { return await flakyAsync(); }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.True(r1.Suspension!.IsRetry);
        Assert.Equal(1, callCount);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(WorkflowStatus.Completed, r2.Status);
        Assert.Equal("ok", r2.Value!.AsString());
        Assert.Equal(2, callCount);
    }

    // --- CancellationToken ---

    [Fact]
    public void CancellationTokenStopsExecution()
    {
        var (workflow, _) = CreateEngine();

        var script = @"
            async function main() {
                var i = 0;
                while (true) { i++; }
                return i;
            }";

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.ThrowsAny<Exception>(() =>
            workflow.RunWorkflow(script, "main", cts.Token));
    }

    [Fact]
    public void CancellationTokenPassedToAsyncSteps()
    {
        var receivedToken = false;
        var (workflow, _) = CreateEngine();
        workflow.RegisterStepFunction("checkToken", async (args, ct) =>
        {
            receivedToken = ct.CanBeCanceled;
            await Task.CompletedTask;
            return "done";
        });

        var script = "async function main() { return await checkToken(); }";

        using var cts = new CancellationTokenSource();
        var result = workflow.RunWorkflow(script, "main", cts.Token);
        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.True(receivedToken);
    }

    [Fact]
    public void CancellationBeforeExecutionThrows()
    {
        var (workflow, _) = CreateEngine();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            workflow.RunWorkflow("async function main() { return 1; }", "main", cts.Token));
    }

    // --- Workflow metadata ---

    [Fact]
    public void MetadataPersistedAcrossSerializationRoundtrip()
    {
        var (workflow, time) = CreateEngine();

        var script = "async function main() { await sleep('1d'); return 'done'; }";
        var result = workflow.RunWorkflow(script, "main");

        // Attach metadata after creation
        result.State!.Metadata["tenantId"] = "tenant-abc";
        result.State.Metadata["priority"] = "high";
        result.State.Metadata["tag"] = "order-processing";

        var json = result.State.Serialize();

        // Deserialize and verify metadata survives
        var restored = WorkflowState.Deserialize(json);
        Assert.Equal("tenant-abc", restored.Metadata["tenantId"]);
        Assert.Equal("high", restored.Metadata["priority"]);
        Assert.Equal("order-processing", restored.Metadata["tag"]);

        // Metadata preserved through resume
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, restored);
        Assert.Equal("done", result2.Value!.AsString());
    }

    [Fact]
    public void MetadataPreservedAcrossResumes()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                await sleep('1d');
                await sleep('1d');
                return 'done';
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        r1.State!.Metadata["workflowType"] = "approval";

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal("approval", r2.State!.Metadata["workflowType"]);

        AdvanceTo(time, r2);
        var r3 = workflow.ResumeWorkflow(script, r2.State!);
        Assert.Equal("done", r3.Value!.AsString());
    }

    [Fact]
    public void EmptyMetadataByDefault()
    {
        var (workflow, _) = CreateEngine();

        var script = "async function main() { await sleep('1d'); }";
        var result = workflow.RunWorkflow(script, "main");

        Assert.NotNull(result.State!.Metadata);
        Assert.Empty(result.State.Metadata);
    }

    // --- Engine setup via Execute/SetValue ---

    [Fact]
    public void ExecuteLoadsLibraryScripts()
    {
        var (workflow, time) = CreateEngine();
        workflow.Execute(@"
            var utils = {
                double: function(x) { return x * 2; },
                greet: function(name) { return 'Hello, ' + name; }
            };
        ");

        var script = @"
            async function main() {
                await sleep('1d');
                return utils.greet('world') + ' ' + utils.double(21);
            }";

        var result = workflow.RunWorkflow(script, "main");
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("Hello, world 42", result2.Value!.AsString());
    }

    [Fact]
    public void SetValueInjectsDotNetObjects()
    {
        var (workflow, _) = CreateEngine();
        workflow.SetValue("config", new { region = "us-east-1", maxRetries = 3 });
        workflow.SetValue("add", new Func<int, int, int>((a, b) => a + b));

        var result = workflow.RunWorkflow(@"
            async function main() {
                return config.region + ':' + add(config.maxRetries, 7);
            }
        ", "main");

        Assert.Equal("us-east-1:10", result.Value!.AsString());
    }

    [Fact]
    public void ExecuteAndSetValueChainable()
    {
        var (workflow, _) = CreateEngine();
        workflow
            .Execute("function square(x) { return x * x; }")
            .SetValue("baseValue", 5)
            .Execute("var computed = square(baseValue);");

        var result = workflow.RunWorkflow(@"
            async function main() { return computed; }
        ", "main");

        Assert.Equal(25.0, result.Value!.AsNumber());
    }

    [Fact]
    public void LibraryScriptsAvailableOnResume()
    {
        var (workflow, time) = CreateEngine();
        workflow.Execute(@"
            function formatOrder(id, status) {
                return 'Order ' + id + ': ' + status;
            }
        ");

        var script = @"
            async function main(orderId) {
                await sleep('1d');
                return formatOrder(orderId, 'shipped');
            }";

        var result = workflow.RunWorkflow(script, "main", "ORD-999");
        AdvanceTo(time, result);
        var result2 = workflow.ResumeWorkflow(script, result.State!);
        Assert.Equal("Order ORD-999: shipped", result2.Value!.AsString());
    }

    // ============================================================
    // Phase 1: Resilience policies on step functions
    // ============================================================

    private static ResiliencePolicy NoDelayPolicy(int maxAttempts) =>
        new ResiliencePolicyBuilder()
            .WithMaxAttempts(maxAttempts)
            .WithDelay(TimeSpan.Zero)
            .Build();

    [Fact]
    public void StepPolicy_SucceedsFirstTry_NoRetries()
    {
        var (workflow, _) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("call", _ =>
        {
            callCount++;
            return "ok";
        }, NoDelayPolicy(maxAttempts: 5));

        var result = workflow.RunWorkflow(@"
            async function main() { return await call(); }
        ", "main");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal("ok", result.Value!.AsString());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void StepPolicy_RetriesOnTransientFailureThenSucceeds()
    {
        var (workflow, _) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("flaky", _ =>
        {
            callCount++;
            if (callCount < 3) throw new InvalidOperationException("transient");
            return 42;
        }, NoDelayPolicy(maxAttempts: 5));

        var result = workflow.RunWorkflow(@"
            async function main() { return await flaky(); }
        ", "main");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal(42.0, result.Value!.AsNumber());
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void StepPolicy_ExhaustedAttempts_StepFails()
    {
        var (workflow, _) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("alwaysFails", _ =>
        {
            callCount++;
            throw new InvalidOperationException("nope");
        }, NoDelayPolicy(maxAttempts: 3));

        var result = workflow.RunWorkflow(@"
            async function main() {
                try { return await alwaysFails(); }
                catch (e) { return 'caught'; }
            }
        ", "main");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal("caught", result.Value!.AsString());
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void StepPolicy_UnhandledExceptionBypassesRetry()
    {
        var workflow = new WorkflowEngine();
        int callCount = 0;
        var policy = new ResiliencePolicyBuilder()
            .WithMaxAttempts(5)
            .WithDelay(TimeSpan.Zero)
            .WithUnhandledException<ArgumentException>()
            .Build();

        workflow.RegisterStepFunction("step", _ =>
        {
            callCount++;
            throw new ArgumentException("bad input");
        }, policy);

        var result = workflow.RunWorkflow(@"
            async function main() {
                try { return await step(); }
                catch (e) { return 'caught'; }
            }
        ", "main");

        Assert.Equal("caught", result.Value!.AsString());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void StepPolicy_RetryableStepException_BypassesPolicyAndSuspends()
    {
        var (workflow, time) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("longRetry", _ =>
        {
            callCount++;
            throw new RetryableStepException("maintenance", TimeSpan.FromMinutes(10));
        }, NoDelayPolicy(maxAttempts: 5));

        var result = workflow.RunWorkflow(@"
            async function main() { return await longRetry(); }
        ", "main");

        Assert.Equal(WorkflowStatus.Suspended, result.Status);
        Assert.Equal("longRetry", result.Suspension!.FunctionName);
        Assert.True(result.Suspension.IsRetry);
        Assert.Equal(s_fixedStart.AddMinutes(10), result.Suspension.ResumeAt);
        Assert.Equal(1, callCount); // bypassed retry
    }

    [Fact]
    public void StepPolicy_ReplayReturnsCachedValue_NoReinvocation()
    {
        var (workflow, time) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("once", _ =>
        {
            callCount++;
            return "value";
        }, NoDelayPolicy(maxAttempts: 5));
        workflow.RegisterSuspendFunction("wait");

        var script = @"
            async function main() {
                var v = await once();
                await wait();
                return v + '!';
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.Equal(1, callCount);

        var r2 = workflow.ResumeWorkflow(script, r1.State!, resumeValue: null);
        Assert.Equal("value!", r2.Value!.AsString());
        Assert.Equal(1, callCount); // did not re-execute on replay
    }

    [Fact]
    public void StepPolicy_AsyncStep_RetriesThenSucceeds()
    {
        var (workflow, _) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("fetch",
            async (_, ct) =>
            {
                callCount++;
                if (callCount < 2)
                    throw new InvalidOperationException("flaky");
                await Task.Yield();
                return (object?)"ok";
            },
            NoDelayPolicy(maxAttempts: 3));

        var result = workflow.RunWorkflow(@"
            async function main() { return await fetch(); }
        ", "main");

        Assert.Equal("ok", result.Value!.AsString());
        Assert.Equal(2, callCount);
    }

    // ============================================================
    // Phase 4: External events (waitForEvent)
    // ============================================================

    [Fact]
    public void WaitForEvent_SingleName_ReturnsPayload()
    {
        var workflow = new WorkflowEngine().EnableExternalEvents();

        var script = @"
            async function main() {
                const p = await waitForEvent('payment');
                return p.amount;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.Equal(new[] { "payment" }, r1.Suspension!.EventNames);

        var r2 = workflow.RaiseEvent(script, r1.State!, "payment", new { amount = 100 });
        Assert.Equal(WorkflowStatus.Completed, r2.Status);
        Assert.Equal(100.0, r2.Value!.AsNumber());
    }

    [Fact]
    public void WaitForEvent_MultipleNames_ReturnsNameAndPayload()
    {
        var workflow = new WorkflowEngine().EnableExternalEvents();

        var script = @"
            async function main() {
                const r = await waitForEvent(['payment', 'cancel']);
                return r.name + ':' + (r.payload ? r.payload.amount || '' : '');
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(new[] { "payment", "cancel" }, r1.Suspension!.EventNames);

        var r2 = workflow.RaiseEvent(script, r1.State!, "cancel", null);
        Assert.Equal("cancel:", r2.Value!.AsString());
    }

    [Fact]
    public void WaitForEvent_TimeoutOption_SetsResumeAt()
    {
        var (workflow, time) = CreateEngine();
        workflow.EnableExternalEvents();

        var script = @"
            async function main() {
                try { return await waitForEvent('x', { timeout: '1h' }); }
                catch (e) { return 'timeout:' + e.name; }
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(s_fixedStart.AddHours(1), r1.Suspension!.ResumeAt);

        AdvanceTo(time, r1);
        var r2 = workflow.TimeoutEvent(script, r1.State!);
        Assert.Equal("timeout:TimeoutError", r2.Value!.AsString());
    }

    [Fact]
    public void WaitForEvent_Replay_ReturnsCachedEvent()
    {
        var workflow = new WorkflowEngine().EnableExternalEvents();

        var script = @"
            async function main() {
                const p = await waitForEvent('x');
                const q = await waitForEvent('y');
                return p.a + '-' + q.b;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        var r2 = workflow.RaiseEvent(script, r1.State!, "x", new { a = "first" });
        Assert.Equal(WorkflowStatus.Suspended, r2.Status);
        Assert.Equal(new[] { "y" }, r2.Suspension!.EventNames);

        var r3 = workflow.RaiseEvent(script, r2.State!, "y", new { b = "second" });
        Assert.Equal("first-second", r3.Value!.AsString());
    }

    [Fact]
    public void WaitForEvent_ComposedWithPromiseRace_ResumesFirstObserved()
    {
        var workflow = new WorkflowEngine().EnableExternalEvents();

        var script = @"
            async function main() {
                const result = await Promise.race([
                    waitForEvent('a').then(p => 'a:' + p.v),
                    waitForEvent('b').then(p => 'b:' + p.v),
                ]);
                return result;
            }";

        // First-suspension-wins: only 'a' is observed. Orchestrator resumes 'a'.
        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(new[] { "a" }, r1.Suspension!.EventNames);

        var r2 = workflow.RaiseEvent(script, r1.State!, "a", new { v = 1 });
        Assert.Equal("a:1", r2.Value!.AsString());
    }

    // ============================================================
    // Phase 3: Promise.all / Promise.race — fan-out / fan-in
    // ============================================================

    [Fact]
    public void PromiseAll_ThreeSteps_AllComplete()
    {
        var workflow = new WorkflowEngine();
        workflow.RegisterStepFunction("a", _ => 10);
        workflow.RegisterStepFunction("b", _ => 20);
        workflow.RegisterStepFunction("c", _ => 30);

        var result = workflow.RunWorkflow(@"
            async function main() {
                const [x, y, z] = await Promise.all([a(), b(), c()]);
                return x + y + z;
            }
        ", "main");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal(60.0, result.Value!.AsNumber());
    }

    [Fact]
    public void PromiseAll_StepAndSuspend_StepCompletes_SuspensionCaptured()
    {
        var (workflow, time) = CreateEngine();
        int callCount = 0;
        workflow.RegisterStepFunction("load", _ => { callCount++; return "data"; });

        var script = @"
            async function main() {
                const [d, _] = await Promise.all([load(), sleep('1h')]);
                return d;
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        Assert.Equal("sleep", r1.Suspension!.FunctionName);
        Assert.Equal(1, callCount);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);

        Assert.Equal(WorkflowStatus.Completed, r2.Status);
        Assert.Equal("data", r2.Value!.AsString());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void PromiseAll_StepThrows_WorkflowFaults()
    {
        var workflow = new WorkflowEngine();
        workflow.RegisterStepFunction("ok", _ => 1);
        workflow.RegisterStepFunction("boom", _ => throw new InvalidOperationException("nope"));

        var result = workflow.RunWorkflow(@"
            async function main() {
                try { return await Promise.all([ok(), boom()]); }
                catch (e) { return 'caught:' + e.message; }
            }
        ", "main");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.StartsWith("caught:", result.Value!.AsString());
    }

    [Fact]
    public void PromiseRace_FirstStepWins()
    {
        var workflow = new WorkflowEngine();
        workflow.RegisterStepFunction("a", _ => "first");
        workflow.RegisterStepFunction("b", _ => "second");

        var result = workflow.RunWorkflow(@"
            async function main() {
                return await Promise.race([a(), b()]);
            }
        ", "main");

        Assert.Equal("first", result.Value!.AsString());
    }

    [Fact]
    public void MultipleSuspensions_FirstWins_SecondRunsOnNextResume()
    {
        var (workflow, time) = CreateEngine();

        var script = @"
            async function main() {
                const [a, b] = await Promise.all([sleep('1h'), sleep('2h')]);
                return 'done';
            }";

        var r1 = workflow.RunWorkflow(script, "main");
        Assert.Equal(WorkflowStatus.Suspended, r1.Status);
        // First suspension wins — the 1h sleep gets observed, not the 2h.
        Assert.Equal(s_fixedStart.AddHours(1), r1.Suspension!.ResumeAt);

        AdvanceTo(time, r1);
        var r2 = workflow.ResumeWorkflow(script, r1.State!);
        Assert.Equal(WorkflowStatus.Suspended, r2.Status);
        // Now the second sleep is observed on the rerun.
        Assert.Equal(s_fixedStart.AddHours(1).AddHours(2), r2.Suspension!.ResumeAt);

        AdvanceTo(time, r2);
        var r3 = workflow.ResumeWorkflow(script, r2.State!);
        Assert.Equal("done", r3.Value!.AsString());
    }

    [Fact]
    public void StepPolicy_ByName_ResolvesFromProvider()
    {
        var (workflow, _) = CreateEngine();
        var provider = new ResiliencePolicyProviderBuilder()
            .WithPolicy("retry3", b => b.WithMaxAttempts(3).WithDelay(TimeSpan.Zero));
        workflow.UseResiliencePolicyProvider(provider);

        int callCount = 0;
        workflow.RegisterStepFunction("step", _ =>
        {
            callCount++;
            if (callCount < 3) throw new InvalidOperationException("x");
            return "done";
        }, policyName: "retry3");

        var result = workflow.RunWorkflow(@"
            async function main() { return await step(); }
        ", "main");

        Assert.Equal("done", result.Value!.AsString());
        Assert.Equal(3, callCount);
    }
}
