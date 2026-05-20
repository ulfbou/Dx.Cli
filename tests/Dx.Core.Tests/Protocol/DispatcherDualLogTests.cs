using Dapper;

using Dx.Core;
using Dx.Core.Execution.Results;
using Dx.Core.Genesis;
using Dx.Core.Protocol;

using Microsoft.Data.Sqlite;

using Xunit;

namespace Dx.Core.Tests.Protocol;

public class DispatcherDualLogTests
{
    [Fact]
    public async Task ParseFailure_Writes_Exactly_Two_Logs()
    {
        // Arrange — create temp workspace and session
        var root = Path.Combine(Path.GetTempPath(), "dx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        WorkspaceInitializer.Initialize(root);
        await using var conn = await DxDatabase.OpenAsync(root);
        await DxDatabase.MigrateAsync(conn);

        var sessionId = "s1";
        await SessionGenesisCreator.CreateAsync(conn, root, sessionId, null, null, false);

        var runtime = await DxRuntime.OpenAsync(root, sessionId);
        var badDx = "this is not a DX document"; // missing %%DX header

        // Act — call through public API (no direct parser)
        var result = await runtime.ApplyAsync(badDx, "user", isDryRun: false);

        // Assert — result is normalized validation failure
        Assert.Equal(DxResultStatus.ValidationFailure, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Single(result.Diagnostics);
        Assert.Equal("DX-PARSE-001", result.Diagnostics[0].Code);
        Assert.Contains("Document must begin with %%DX", result.Diagnostics[0].Message);

        // Assert — dual-log invariant
        List<LogRow> logs = (await conn.QueryAsync<LogRow>(
            "SELECT * FROM session_log WHERE session_id = @sid AND direction = 'user' ORDER BY id",
            new { sid = sessionId })).ToList();

        Assert.Equal(2, logs.Count); // intent + result

        // 1️⃣ Intent log
        var intent = logs[0];
        Assert.Equal("user", intent.direction);
        Assert.Equal(badDx, intent.document);
        Assert.Null(intent.tx_success);
        Assert.Null(intent.snap_handle);

        // 2️⃣ Result log
        var output = logs[1];
        Assert.Equal("user", output.direction);
        Assert.Equal(0, output.tx_success);
        Assert.Null(output.snap_handle);
        Assert.Contains("status=validationfailure", output.document.ToLowerInvariant());
        Assert.Contains("dx-parse-001", output.document.ToLowerInvariant());
    }

    private sealed class LogRow
    {
        public int id { get; set; }
        public string direction { get; set; } = "";
        public string document { get; set; } = "";
        public int? tx_success { get; set; }
        public string? snap_handle { get; set; }
    }
}
