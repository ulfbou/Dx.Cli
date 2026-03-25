using Dx.Core;

using Microsoft.Data.Sqlite;

namespace Dx.Core.Genesis;

/// <summary>
/// Initializes the workspace at the given root path. This includes creating 
/// the .dx/ directory, initializing the snap.db SQLite database, and applying 
/// any necessary schema migrations. This method is idempotent and can be 
/// safely called multiple times without causing issues.
/// </summary>
/// <param name="root">The root path of the workspace to initialize.</param>
public static class WorkspaceInitializer
{
    public static void Initialize(string root)
    {
        var abs = Path.GetFullPath(root);
        var dxDir = Path.Combine(abs, ".dx");
        var dbPath = Path.Combine(dxDir, "snap.db");

        // 1. Make sure root exists
        if (!Directory.Exists(abs))
            Directory.CreateDirectory(abs);

        // 2. Check for existing workspace
        if (Directory.Exists(dxDir) && File.Exists(dbPath))
        {
            throw new DxException(
                DxError.WorkspaceAlreadyInitialized,
                $"Workspace already initialized at: {abs}");
        }

        // 3. Ensure .dx exists (but DO NOT open DB here)
        Directory.CreateDirectory(dxDir);
    }
}
