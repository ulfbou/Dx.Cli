using Dx.Core;
using Dx.Core.Protocol;

using System.Reflection;

using Xunit;

namespace Dx.Core.Tests;

/// <summary>
/// Contract-guard tests that enforce the convergence invariant: DxRuntime has no
/// execution authority. All execution MUST flow through DxDispatcher.
/// </summary>
/// <remarks>
/// These tests use reflection so they remain valid even if method signatures change.
/// They are intentionally fragile to execution-authority additions — any new
/// Execute/Apply/Dispatch/Parse method on DxRuntime must be reviewed against #45.
/// </remarks>
public class DxRuntimeExecutionAuthorityTests
{
    private static readonly MethodInfo[] _publicMethods;

    static DxRuntimeExecutionAuthorityTests()
    {
        // DxRuntime is intentionally internal; obtain the Type via its assembly to allow
        // reflection assertions regardless of protection level.
        var asm = typeof(DxDispatcher).Assembly;
        var runtimeType = asm.GetType("Dx.Core.DxRuntime")
            ?? throw new InvalidOperationException("DxRuntime type not found in Dx.Core assembly.");

        _publicMethods = runtimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
    }

    [Fact]
    public void DxRuntime_HasNo_ExecutionAuthorityMethods()
    {
        // Any public method whose name starts with one of these prefixes would indicate
        // that DxRuntime has reclaimed execution authority from DxDispatcher.
        var forbiddenPrefixes = new[]
        {
            "Execute", "Apply", "Dispatch", "ParseText", "Parse",
            "RunAsync", "Checkout", "Materialize"
        };

        var violations = _publicMethods
            .Where(m => forbiddenPrefixes.Any(p =>
                m.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"DxRuntime must not expose execution-authority methods. " +
            $"Found: {string.Join(", ", violations)}. " +
            $"Move these to DxDispatcher (see #45).");
    }

    [Fact]
    public void DxRuntime_PublicMembers_AreQueryOrFactoryOnly()
    {
        // Classify every public non-constructor member. Permitted categories:
        //   - Factory:  OpenAsync, Create, IsWorkspace
        //   - Queries:  ListSnaps, GetHead, GetSnapFiles, Diff, GetLog, IgnoreSet
        var permittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Factories
            "OpenAsync", "Create", "IsWorkspace",
            // Queries
            "ListSnaps", "GetHead", "GetSnapFiles", "Diff", "GetLog",
            // Property
            "IgnoreSet",
            // Object built-ins
            "ToString", "GetHashCode", "Equals", "GetType",
        };

        var unexpectedPublicMembers = _publicMethods
            .Where(m => !permittedNames.Contains(m.Name))
            // Exclude compiler-generated property accessors (get_/set_); the property
            // name itself is validated by its presence in permittedNames above.
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            unexpectedPublicMembers.Count == 0,
            $"DxRuntime has unexpected public members that may indicate restored execution authority: " +
            $"{string.Join(", ", unexpectedPublicMembers)}. " +
            $"Review against convergence spec §7 and issue #45.");
    }
}
