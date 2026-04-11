using Dx.Core.Execution;
using Dx.Core.Execution.Adapters;

using FluentAssertions;

using System;

using Xunit;

using ExecutionContext = Dx.Core.Execution.ExecutionContext;

namespace Dx.Core.Tests.Execution.Results;

/// <summary>
/// Specification tests for execution result lifecycle enforcement.
/// </summary>
public sealed class DxResultAssignmentGuardTests
{
    [Fact(DisplayName = "ExecutionContext allows exactly one result assignment")]
    public void ExecutionContext_Should_Allow_Exactly_One_Result_Assignment()
    {
        // Arrange
        var context = new ExecutionContext();
        var firstResult = DxResultMapper.FromSuccess("T0001");

        // Act
        context.SetDxResult(firstResult);

        // Assert
        context.DxResult.Should().NotBeNull();
        context.DxResult.Should().BeSameAs(firstResult);
    }

    [Fact(DisplayName = "ExecutionContext throws when result is assigned more than once")]
    public void ExecutionContext_Should_Throw_On_Reassignment()
    {
        // Arrange
        var context = new ExecutionContext();
        var result = DxResultMapper.FromSuccess("T0001");

        context.SetDxResult(result);

        // Act
        Action act = () => context.SetDxResult(result);

        // Assert
        act.Should()
           .Throw<InvalidOperationException>()
           .WithMessage("*cannot be reassigned*");
    }

    [Fact(DisplayName = "ExecutionContext rejects null result assignment")]
    public void ExecutionContext_Should_Reject_Null_Result()
    {
        // Arrange
        var context = new ExecutionContext();

        // Act
        Action act = () => context.SetDxResult(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}
