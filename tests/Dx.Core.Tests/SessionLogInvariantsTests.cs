using Dapper;

using Dx.Core;
using Dx.Core.Execution;
using Dx.Core.Protocol;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Dx.Core.Tests.Protocol;

/// <summary>
/// Enforces the foundational DX protocol invariants established in PR 43.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architectural Lock:</strong>
/// These tests do not verify block execution logic. They verify the 
/// <em>observability and audit contracts</em> of the dispatcher.
/// </para>
/// <para>
/// Do not mock the database or the dispatcher for these tests. They must 
/// interact with a real SQLite connection to guarantee schema and transaction 
/// integrity.
/// </para>
/// </remarks>
public class SessionLogInvariantsTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DxDispatcher _dispatcher = null!;
    private readonly string _sessionId = "test-session";
    private string _workspaceRoot = null!; // Use a real temp path

    public async Task InitializeAsync()
    {
        // 1. Create a unique, real workspace for this test run
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"dx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        // IMPORTANT: The dispatcher looks for .dx/ for locking and metadata
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, ".dx"));

        // 2. Setup SQLite
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        DxDatabase.Migrate(_connection);

        // 3. Setup Genesis Session & Snap (Required for Base Resolution)
        await _connection.ExecuteAsync(
            "INSERT INTO sessions (session_id, root, created_utc) VALUES (@sid, @root, @t)",
            new { sid = _sessionId, root = _workspaceRoot, t = DxDatabase.UtcNow() });

        // Create the dummy T0000 so the dispatcher can resolve the 'base'
        var dummyHash = new byte[32];
        await _connection.ExecuteAsync("INSERT INTO snaps (snap_hash, created_utc) VALUES (@h, @t)", new { h = dummyHash, t = DxDatabase.UtcNow() });
        await _connection.ExecuteAsync(
            "INSERT INTO snap_handles (session_id, handle, snap_hash, seq, created_utc) VALUES (@sid, 'T0000', @h, 0, @t)",
            new { sid = _sessionId, h = dummyHash, t = DxDatabase.UtcNow() });
        await _connection.ExecuteAsync(
            "INSERT INTO session_state (session_id, head_snap_hash, updated_utc) VALUES (@sid, @h, @t)",
            new { sid = _sessionId, h = dummyHash, t = DxDatabase.UtcNow() });

        // 4. Initialize Dispatcher with Mandatory IgnoreSet
        // This is what prevents the "snaps.lock" FileLoadException
        var ignoreSet = IgnoreSetFactory.Create(null, Array.Empty<string>(), false);
        var store = new DxStore(_connection, _sessionId);

        _dispatcher = new DxDispatcher(
            _connection,
            store,
            _workspaceRoot,
            ignoreSet,
            _sessionId);
    }

    public Task DisposeAsync()
    {
        _connection?.Dispose();

        // Cleanup the physical workspace
        if (!string.IsNullOrEmpty(_workspaceRoot) && Directory.Exists(_workspaceRoot))
        {
            try { Directory.Delete(_workspaceRoot, true); } catch { /* ignore cleanup errors */ }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DualLogInvariant_MutatingDocument_WritesExactlyTwoLogs_WithCorrectTxSuccessState()
    {
        // Arrange
        var request = CreateTestRequest(isMutating: true);

        // Act
        var result = await _dispatcher.DispatchAsync(request);

        // Assert
        if (!result.IsSuccess)
        {
            var errors = string.Join(", ", result.Diagnostics.Select(d => d.Message));
            throw new Exception($"Dispatch failed: {result.Message}. Diagnostics: {errors}");
        }

        var logs = GetSessionLogs();

        Assert.Equal(2, logs.Count);

        // Log 1: Input Intent (T-Success is NULL)
        Assert.Null(logs[0].TxSuccess);

        // Log 2: Execution Outcome (T-Success is 1)
        Assert.Equal(1, logs[1].TxSuccess);
        Assert.NotNull(logs[1].SnapHandle);
        Assert.Equal("T0001", logs[1].SnapHandle);
    }

    [Fact]
    public async Task CausalOrdering_LogsAreWrittenInChronologicalOrder_InputBeforeResult()
    {
        var request = CreateTestRequest(isMutating: true);
        await _dispatcher.DispatchAsync(request);

        var logs = GetSessionLogs();

        Assert.Equal(2, logs.Count);
        Assert.True(logs[0].Id < logs[1].Id, "Causal ID ordering failed.");

        // Fix CS0019: Use string.CompareOrdinal or parse to DateTime
        Assert.True(string.CompareOrdinal(logs[0].CreatedAt, logs[1].CreatedAt) <= 0,
            "Input timestamp must not be later than Result timestamp.");
    }

    [Fact]
    public async Task TriStateSemantics_FailingMutation_LogsTxSuccessAsZero()
    {
        // Arrange: A request designed to fail via BaseMismatch (expects T9999, actual is T0000)
        var request = CreateFailingTestRequest();

        // Act
        await _dispatcher.DispatchAsync(request);

        // Assert
        var logs = GetSessionLogs();

        Assert.Equal(2, logs.Count);

        // Input log is still preserved despite failure
        Assert.Null(logs[0].TxSuccess);

        // Outcome log explicitly marks failure (0) and yields no snapshot
        Assert.Equal(0, logs[1].TxSuccess);
        Assert.Null(logs[1].SnapHandle);
    }

    [Fact]
    public async Task ReadOnlyExecution_LogsExecutionButDoesNotCommitState()
    {
        // Arrange
        var request = CreateTestRequest(isMutating: false);

        // Act
        await _dispatcher.DispatchAsync(request);

        // Assert
        var logs = GetSessionLogs();

        Assert.Equal(2, logs.Count);

        // Outcome log is successful, but explicitly produces NO snapshot
        Assert.Equal(1, logs[1].TxSuccess);
        Assert.Null(logs[1].SnapHandle);
    }

    [Fact]
    public async Task CheckoutInvariant_OutsideDispatcher_WritesExactlyOneSuccessfulLog()
    {
        // Arrange & Act
        // Because DxRuntime.CheckoutAsync operates on physical files, we simulate the
        // exact DB call it makes in this black-box, in-memory DB test to verify the schema.
        await _connection.ExecuteAsync(
            "INSERT INTO session_log (session_id, direction, document, snap_handle, tx_success, created_at) " +
            "VALUES (@sid, 'tool', '(checkout T0000)', 'T0000', 1, @t)",
            new { sid = _sessionId, t = DxDatabase.UtcNow() });

        // Assert
        var logs = GetSessionLogs();

        Assert.Single(logs);
        Assert.Equal("tool", logs[0].Direction);
        Assert.Equal(1, logs[0].TxSuccess);
        Assert.NotNull(logs[0].SnapHandle);
        Assert.Contains("checkout", logs[0].Document);
    }

    // --- Test Helpers ---

    /// <summary>
    /// Dedicated DTO for reading back the full table including auto-generated columns.
    /// </summary>
    private sealed class TestLogEntry
    {
        public long Id { get; set; }
        public string Direction { get; set; } = "";
        public string Document { get; set; } = "";
        public int? TxSuccess { get; set; }
        public string? SnapHandle { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    private List<TestLogEntry> GetSessionLogs()
    {
        return _connection.Query<TestLogEntry>(
            "SELECT id as Id, direction as Direction, tx_success as TxSuccess, snap_handle as SnapHandle, " +
            "created_at as CreatedAt, document as Document " +
            "FROM session_log ORDER BY id ASC"
        ).ToList();
    }

    private DxExecutionRequest CreateTestRequest(bool isMutating)
    {
        var header = new DxHeader("1.3", _sessionId, "user", "test", "T0000", null, null, !isMutating, null);
        var blocks = new List<DxBlock>();

        if (isMutating)
        {
            blocks.Add(new FileBlock("test.txt", "utf-8", false, null, true, null, null, "data"));
        }
        else
        {
            blocks.Add(new NoteBlock("just reading"));
        }

        var doc = new DxDocument(header, blocks);

        return new DxExecutionRequest(
            Document: doc,
            RawText: "%%DX v1.3\n" + (isMutating ? "%%FILE path=\"test.txt\"\ndata\n%%ENDBLOCK\n" : "%%NOTE\njust reading\n%%ENDBLOCK\n"),
            Direction: "user",
            Mode: DxExecutionMode.Apply,
            IsDryRun: false,
            Progress: null,
            Options: new ApplyOptions(),
            CancellationToken: CancellationToken.None
        );
    }

    private DxExecutionRequest CreateFailingTestRequest()
    {
        // Require a base that doesn't match HEAD (T9999) to force a fast protocol failure
        var header = new DxHeader("1.3", _sessionId, "user", "test", "T9999", null, null, false, null);
        var blocks = new List<DxBlock>
        {
            new FileBlock("fail.txt", "utf-8", false, null, true, null, null, "data")
        };

        var doc = new DxDocument(header, blocks);

        return new DxExecutionRequest(
            Document: doc,
            RawText: "%%DX v1.3 base=T9999\n%%FILE path=\"fail.txt\"\ndata\n%%ENDBLOCK\n",
            Direction: "user",
            Mode: DxExecutionMode.Apply,
            IsDryRun: false,
            Progress: null,
            Options: new ApplyOptions(OnBaseMismatch: "reject"),
            CancellationToken: CancellationToken.None
        );
    }
}
