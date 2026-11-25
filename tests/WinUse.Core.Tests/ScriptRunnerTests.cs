using System.Text.Json;
using WinUse.Core.Audit;
using WinUse.Core.Capabilities;
using WinUse.Core.Dispatch;
using WinUse.Core.IdempotencyFlow;
using WinUse.Core.Policy;
using WinUse.Core.Protocol;
using WinUse.Core.Scripts;

namespace WinUse.Core.Tests;

public class ScriptRunnerTests : IDisposable
{
    private const string OkCapability = "test.ok";
    private const string FailCapability = "test.fail";

    private readonly string _dir;
    private readonly ScriptRunner _runner;

    public ScriptRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winuse-runner-tests-" + Guid.NewGuid().ToString("N"));

        var descriptors = new CapabilityDescriptor[]
        {
            new(OkCapability, "v1", Tier.Core, RiskLevel.R0, Idempotency.I,
                new[] { Layer.UIA }, "测试成功能力"),
            new(FailCapability, "v1", Tier.Core, RiskLevel.R0, Idempotency.N,
                new[] { Layer.UIA }, "测试失败能力"),
        };
        ICapability[] implementations =
        {
            new FakeCapability(OkCapability, succeed: true),
            new FakeCapability(FailCapability, succeed: false),
        };

        var registry = new CapabilityRegistry(descriptors, implementations);
        var dispatcher = new CapabilityDispatcher(
            registry,
            new SessionState(),
            new RiskClassifier(),
            new ApprovalGate(new HeadlessApprovalHandler()),
            new IdempotencyStore(),
            new AuditWriter(Path.Combine(_dir, "audit")),
            new EmptyServiceProvider());

        _runner = new ScriptRunner(dispatcher);
    }

    private static ScriptStep Step(string capability) =>
        new(capability, JsonSerializer.SerializeToElement(new Dictionary<string, object?>()));

    [Fact]
    public async Task All_ok_succeeds()
    {
        var doc = new ScriptDocument("s", "", "",
            new[] { Step(OkCapability), Step(OkCapability) }, "abort");

        var result = await _runner.RunAsync(doc);

        Assert.True(result.Ok);
        Assert.Equal(2, result.StepsCompleted);
        Assert.Null(result.FailedAt);
    }

    [Fact]
    public async Task Abort_stops_at_first_failure()
    {
        var doc = new ScriptDocument("s", "", "",
            new[] { Step(OkCapability), Step(FailCapability), Step(OkCapability) }, "abort");

        var result = await _runner.RunAsync(doc);

        Assert.False(result.Ok);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(2, result.Trace.Count);
        Assert.NotNull(result.FailedAt);
        Assert.Equal(2, result.FailedAt!.Index);
    }

    [Fact]
    public async Task Continue_runs_all_steps()
    {
        var doc = new ScriptDocument("s", "", "",
            new[] { Step(OkCapability), Step(FailCapability), Step(OkCapability) }, "continue");

        var result = await _runner.RunAsync(doc);

        Assert.False(result.Ok);
        Assert.Equal(2, result.StepsCompleted);
        Assert.Equal(3, result.Trace.Count);
        Assert.Null(result.FailedAt);
    }

    [Fact]
    public async Task Rollback_policy_is_noted_and_aborts()
    {
        var doc = new ScriptDocument("s", "", "",
            new[] { Step(FailCapability), Step(OkCapability) }, "rollback");

        var result = await _runner.RunAsync(doc);

        Assert.False(result.Ok);
        Assert.Single(result.Trace);
        Assert.NotNull(result.Note);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private sealed class FakeCapability(string id, bool succeed) : ICapability
    {
        public string Id => id;

        public Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken) =>
            Task.FromResult(succeed
                ? CapabilityResult.Success(new { called = id })
                : CapabilityResult.Failure("ELEMENT_NOT_FOUND", "test failure"));
    }

    /// <summary>测试用空服务容器：fake 能力不解析任何服务。</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
