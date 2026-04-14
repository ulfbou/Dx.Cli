namespace Dx.Core.Protocol;

/// <summary>
/// Carries per-invocation overrides for the apply transaction lifecycle.
/// These take precedence over workspace configuration for a single dispatch call.
/// </summary>
/// <param name="OnBaseMismatch">
/// Overrides <c>conflict.on_base_mismatch</c> for this invocation.
/// <c>"warn"</c> logs a warning and continues; <c>"reject"</c> (or <see langword="null"/>)
/// aborts with <see cref="DxError.BaseMismatch"/>.
/// </param>
/// <param name="RunTimeoutSeconds">
/// Overrides <c>run.run_timeout</c> for this invocation.
/// <see langword="null"/> means use the configured default (usually 0 = no timeout).
/// </param>
public sealed record ApplyOptions(
    string? OnBaseMismatch = null,
    int? RunTimeoutSeconds = null
);
