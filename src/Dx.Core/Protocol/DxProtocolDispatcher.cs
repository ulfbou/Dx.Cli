using Dx.Core.Execution;
using Dx.Core.Execution.Adapters;
using Dx.Core.Execution.Results;

namespace Dx.Core.Protocol;

/// <summary>
/// Provides the single, authoritative protocol entry point for DX execution.
/// </summary>
/// <remarks>
/// <para>
/// This type is the only supported entry point for executing DX documents.
/// All callers — including CLI, tests, and integrations — must route execution through this dispatcher.
/// </para>
/// <para>
/// This dispatcher guarantees:
/// <list type="bullet">
/// <item><description>Exactly one <see cref="DxResult"/> is returned per invocation.</description></item>
/// <item><description>No exceptions escape this boundary.</description></item>
/// <item><description>All execution outcomes are normalized.</description></item>
/// </list>
/// </para>
/// <para>
/// This type is thread-safe and stateless.
/// </para>
/// </remarks>
public sealed class DxProtocolDispatcher
{
    private readonly IDxDispatchEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="DxProtocolDispatcher"/> class.
    /// </summary>
    /// <param name="engine">
    /// The underlying dispatch engine. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="engine"/> is <see langword="null"/>.
    /// </exception>
    public DxProtocolDispatcher(IDxDispatchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <summary>
    /// Executes a DX request.
    /// </summary>
    /// <param name="request">The execution request.</param>
    /// <returns>
    /// A task that resolves to a normalized <see cref="DxResult"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method is terminal. It always returns exactly one <see cref="DxResult"/>.
    /// </para>
    /// <para>
    /// This method never throws. All failures are captured and converted into
    /// <see cref="DxResult"/> instances.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    public async Task<DxResult> ExecuteAsync(DxExecutionRequest request)
    {
        // 1. Transactional Boundary (Audit Start)
        await _engine.BeginPendingAsync(request);

        try
        {
            // 2. Authority: Parsing moved into the protocol boundary
            var (doc, errors) = DxParser.ParseText(request.RawText);
            if (errors.Count > 0) return errors.AsValidationFailure(request.IsDryRun);

            // 3. Authority: Execution & Mutation Sequencing
            var result = await _engine.DispatchAsync(request with { Document = doc });

            // 4. Atomic Commit / Audit Result
            await _engine.CommitAsync(result);
            return result;
        }
        catch (Exception ex)
        {
            var failResult = ex.AsExecutionFailure(request.IsDryRun);
            await _engine.AbortAsync(failResult);
            return failResult;
        }
    }
}
