using System;
using System.Collections.Generic;

using Dx.Core.Execution.Adapters;
using Dx.Core.Execution.Results;

using FluentAssertions;

using Xunit;

namespace Dx.Core.Tests.Execution.Results;

/// <summary>
/// Specification tests for <see cref="DxResultMapper"/>.
/// </summary>
public sealed class DxResultMapperTests
{
    [Fact(DisplayName = "FromSuccess returns a successful, non-dry-run result with no diagnostics")]
    public void FromSuccess_Should_Return_Success_Result()
    {
        // Arrange
        var snapId = "T0001";

        // Act
        var result = DxResultMapper.FromSuccess(snapId);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(DxResultStatus.Success);
        result.SnapId.Should().Be(snapId);
        result.IsDryRun.Should().BeFalse();
        result.Diagnostics.Should().BeEmpty();
        result.IsFailure.Should().BeFalse();
    }

    [Fact(DisplayName = "FromBaseMismatch returns failure result with BASE_MISMATCH diagnostic")]
    public void FromBaseMismatch_Should_Return_BaseMismatch_Result()
    {
        // Arrange
        var expected = "T0001";
        var actual = "T0002";

        // Act
        var result = DxResultMapper.FromBaseMismatch(expected, actual);

        // Assert
        result.Status.Should().Be(DxResultStatus.BaseMismatch);
        result.SnapId.Should().BeNull();
        result.IsFailure.Should().BeTrue();
        result.IsDryRun.Should().BeFalse();

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Satisfy<DxDiagnostic>(d =>
            {
                d.Code.Should().Be("BASE_MISMATCH");
                d.Severity.Should().Be(DxDiagnosticSeverity.Error);
                d.Message.Should().Contain(expected).And.Contain(actual);
            });
    }

    [Fact(DisplayName = "FromValidationErrors materializes diagnostics and returns validation failure")]
    public void FromValidationErrors_Should_Return_ValidationFailure_Result()
    {
        // Arrange
        var diagnostics = new List<DxDiagnostic>
        {
            new("VAL001", "Invalid block", DxDiagnosticSeverity.Error)
        };

        // Act
        var result = DxResultMapper.FromValidationErrors(diagnostics);

        // Assert
        result.Status.Should().Be(DxResultStatus.ValidationFailure);
        result.Diagnostics.Should().HaveCount(1);
        result.Diagnostics.Should().BeEquivalentTo(diagnostics);
        result.IsFailure.Should().BeTrue();
        result.IsDryRun.Should().BeFalse();
    }

    [Fact(DisplayName = "FromValidationErrors tolerates null diagnostics and returns an empty collection")]
    public void FromValidationErrors_Should_Handle_Null_Diagnostics()
    {
        // Act
        var result = DxResultMapper.FromValidationErrors(null);

        // Assert
        result.Status.Should().Be(DxResultStatus.ValidationFailure);
        result.Diagnostics.Should().NotBeNull().And.BeEmpty();
        result.IsFailure.Should().BeTrue();
    }

    [Fact(DisplayName = "FromExecutionException maps exception into execution failure diagnostic")]
    public void FromExecutionException_Should_Return_ExecutionFailure_Result()
    {
        // Arrange
        var ex = new Exception("boom");

        // Act
        var result = DxResultMapper.FromExecutionException(ex);

        // Assert
        result.Status.Should().Be(DxResultStatus.ExecutionFailure);
        result.Diagnostics.Should().ContainSingle();

        var diagnostic = result.Diagnostics[0];
        diagnostic.Code.Should().Be("EXECUTION_FAILURE");
        diagnostic.Message.Should().Be(ex.Message);
        diagnostic.Severity.Should().Be(DxDiagnosticSeverity.Error);

        result.IsFailure.Should().BeTrue();
    }

    [Fact(DisplayName = "FromExecutionException throws when exception is null")]
    public void FromExecutionException_Should_Throw_On_Null_Exception()
    {
        // Act
        Action act = () => DxResultMapper.FromExecutionException(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "FromDryRun returns dry-run success result without diagnostics")]
    public void FromDryRun_Should_Return_DryRun_Result()
    {
        // Act
        var result = DxResultMapper.FromDryRun();

        // Assert
        result.Status.Should().Be(DxResultStatus.DryRun);
        result.IsDryRun.Should().BeTrue();
        result.SnapId.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();

        // Dry-run is treated as non-failure by convention
        result.IsFailure.Should().BeFalse();
    }
}