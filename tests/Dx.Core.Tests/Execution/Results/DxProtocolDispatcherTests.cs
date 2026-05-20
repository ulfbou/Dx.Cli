using Dx.Core.Execution;
using Dx.Core.Execution.Results;
using Dx.Core.Protocol;

using FluentAssertions;

using Xunit;

namespace Dx.Core.Tests.Execution;

public sealed class DxProtocolDispatcherTests
{
    [Fact(DisplayName = "ExecuteAsync maps success result")]
    public async Task ExecuteAsync_Should_Map_Success()
    {
        var engine = new FakeEngine(
            new DispatchResult(
                Success: true,
                NewHandle: "T0001",
                Error: null,
                Operations: Array.Empty<OperationResult>(),
                IsBaseMismatch: false));

        var dispatcher = new DxProtocolDispatcher(engine);

        var document = CreateDocument();
        var request = CreateRequest(document);

        DxResult result = await dispatcher.ExecuteAsync(request);

        result.Status.Should().Be(DxResultStatus.Success);
        result.SnapId.Should().Be("T0001");
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact(DisplayName = "ExecuteAsync maps base mismatch")]
    public async Task ExecuteAsync_Should_Map_BaseMismatch()
    {
        var engine = new FakeEngine(
            new DispatchResult(
                false,
                null,
                "Base mismatch",
                Array.Empty<OperationResult>(),
                IsBaseMismatch: true));

        var dispatcher = new DxProtocolDispatcher(engine);
        var request = CreateRequest(CreateDocument());

        DxResult result = await dispatcher.ExecuteAsync(request);

        result.Status.Should().Be(DxResultStatus.BaseMismatch);
        result.Diagnostics.Should().ContainSingle();
    }

    [Fact(DisplayName = "ExecuteAsync maps execution failure")]
    public async Task ExecuteAsync_Should_Map_Failure()
    {
        var engine = new FakeEngine(
            new DispatchResult(
                false,
                null,
                "Failure",
                Array.Empty<OperationResult>(),
                IsBaseMismatch: false));

        var dispatcher = new DxProtocolDispatcher(engine);
        var request = CreateRequest(CreateDocument(), DxExecutionMode.Apply);

        DxResult result = await dispatcher.ExecuteAsync(request);

        result.Status.Should().Be(DxResultStatus.ExecutionFailure);
        result.Diagnostics.Should().ContainSingle();
    }

    [Fact(DisplayName = "ExecuteAsync captures exceptions")]
    public async Task ExecuteAsync_Should_Handle_Exception()
    {
        var dispatcher = new DxProtocolDispatcher(new ThrowingEngine());
        var request = CreateRequest(CreateDocument(), DxExecutionMode.Apply);

        DxResult result = await dispatcher.ExecuteAsync(request);

        result.Status.Should().Be(DxResultStatus.ExecutionFailure);
        result.Diagnostics.Should().ContainSingle();
    }

    // ──────────────────────────────────────────────────────────────

    private static DxDocument CreateDocument()
        => new(
            new DxHeader(
                Version: "1.0",
                Session: "T0000",
                Author: "TestPilot",
                Title: "Test Document",
                Base: null,
                Root: null,
                Target: null,
                ReadOnly: false,
                ArtifactsDir: null),
            Array.Empty<DxBlock>());

    private sealed class FakeEngine : IDxDispatchEngine
    {
        private readonly DispatchResult _result;

        public FakeEngine(DispatchResult result) => _result = result;

        public Task AbortAsync(DxResult result) => Task.CompletedTask;

        public Task BeginPendingAsync(DxExecutionRequest request) => Task.CompletedTask;

        public Task CommitAsync(DxResult result) => Task.CompletedTask;

        public Task<DispatchResult> DispatchAsync(DxExecutionRequest request) => Task.FromResult(_result);

        Task<DxResult> IDxDispatchEngine.DispatchAsync(DxExecutionRequest request)
        {
            var status = _result.Success ? DxResultStatus.Success
                       : _result.IsBaseMismatch ? DxResultStatus.BaseMismatch
                       : DxResultStatus.ExecutionFailure;

            var diagnostics = new List<DxDiagnostic>();
            if (!_result.Success)
            {
                diagnostics.Add(new DxDiagnostic(
                    code: _result.IsBaseMismatch ? "BASE_MISMATCH" : "EXECUTION_ERROR",
                    message: _result.Error ?? "An unknown execution error occurred.",
                    severity: DxDiagnosticSeverity.Error));
            }

            return Task.FromResult(new DxResult(
                Status: status,
                Message: _result.Error,
                SnapId: _result.NewHandle,
                Diagnostics: diagnostics,
                IsDryRun: false));
        }
    }

    private sealed class ThrowingEngine : IDxDispatchEngine
    {
        public Task AbortAsync(DxResult result) => Task.CompletedTask;

        public Task BeginPendingAsync(DxExecutionRequest request) => Task.CompletedTask;

        public Task CommitAsync(DxResult result) => Task.CompletedTask;

        public Task<DispatchResult> DispatchAsync(DxExecutionRequest request)
            => throw new InvalidOperationException("Boom");

        Task<DxResult> IDxDispatchEngine.DispatchAsync(DxExecutionRequest request)
            => throw new InvalidOperationException("Boom");
    }

    private static DxExecutionRequest CreateRequest(
        DxDocument document,
        DxExecutionMode mode = DxExecutionMode.Apply)
    {
        return new DxExecutionRequest(
            Document: document,
            RawText: "%%DX v1.0\n%%NOTE\ntest\n%%ENDBLOCK\n",
            Direction: "test",
            Mode: mode,
            Progress: null,
            Options: null,
            CancellationToken: default);
    }
}
