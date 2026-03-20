    # Changelog
    
    # Changelog
    
    ## [0.2.0] - 2026-03-19
    
    ### Renamed — `dx` → `dxs`
    
    The binary, tool command name, assembly name, and product name have all been renamed from `dx` to `dxs` to avoid conflicts with any existing `dx` binary on the user's PATH.
    
    - `AssemblyName` changed from `dx` to `dxs`.
    - `Product` changed from `dx` to `dxs`.
    - `PackageProjectUrl` updated to `https://github.com/ulfbou/dx.cli`.
    - `PackageId`, `PackAsTool`, and `ToolCommandName` added to `Dx.Cli.csproj` to publish as a `dotnet tool` (`dxs`).
    - `config.SetApplicationName("dx")` → `config.SetApplicationName("dxs")` in `Program.cs`.
    - All user-facing error messages and command-listing strings in `DxCliErrorRenderer` updated from `dx <subcommand>` to `dxs <subcommand>`.
    - Runtime error messages `"Run 'dx init' first."` → `"Run 'dxs init' first."` in `DxRuntime` and `SessionCommands`.
    - Session log `document` column entry for workspace init changed from `'dx init'` to `'dxs init'`.
    
    ### Fixed — database and lock filenames
    
    - `DxDatabase.Open` previously hardcoded the database filename as `dx.db`. It now accepts a `dbName` parameter defaulting to `snap.db`. All call sites (workspace and global config) now use `snap.db`, matching the schema definition.
    - `DxLock.Acquire` previously created a lock file named `.dx/lock`. It now accepts a `lockName` parameter defaulting to `snaps.lock`, scoping the lock to snapshot operations and avoiding conflicts with sister tools that share the `.dx/` directory.
    - Lock error message now includes the lock filename: `"Session is locked by another process ({lockName})."`.
    - `ConfigCommands.ConfigBaseSettings` `--global` description corrected from `~/.dx/config.db` to `~/.dx/snap.db`.
    - `ConfigCommands.ConfigBaseSettings` `--local` description corrected from `<root>/.dx/dx.db` to `<root>/.dx/snap.db`.
    - `ConfigShowEffectiveCommand` local-database existence check corrected from `dx.db` to `snap.db`.
    - `SessionListCommand` and `SessionNewCommand` workspace existence checks corrected from `dx.db` to `snap.db`.
    - `PackCommand.IsExcluded` internal comment corrected from `subfolder/.dx/dx.db` to `subfolder/.dx/snap.db`.
    
    ### Changed — `dxs log` default limit
    
    - `--limit` default reduced from `50` to `20`. The `[DefaultValue(20)]` attribute is now used exclusively; the field initialiser `= 50` has been removed.
    
    ### Added — short flags on all command options
    
    All `[CommandOption]` entries that lacked a single-character short flag have been given one. The complete set added across all command settings classes:
    
    | Flag | Option | Commands |
    |---|---|---|
    | `-r` | `--root` | All commands |
    | `-s` | `--session` | `apply`, `eval`, `log`, `run`, `snap`, `session show`, `config show-effective` |
    | `-n` | `--dry-run` / `--limit` | `apply`, `log` |
    | `-v` | `--verbose` | `apply`, `init` |
    | `-a` | `--artifacts-dir` | `eval`, `init`, `run`, `session new` |
    | `-x` | `--exclude` | `init`, `session new` |
    | `-b` | `--include-build-output` | `init`, `session new` |
    | `-t` | `--timeout` | `eval`, `run` |
    | `-p` | `--pass-if` / `--path` | `eval`, `snap diff` |
    | `-f` | `--files` / `--file-type` | `snap show`, `pack` |
    | `-o` | `--out` | `pack` |
    | `-m` | `--metadata` | `pack` |
    | `-g` | `--global` | `config *` |
    | `-l` | `--local` | `config *` |
    
    ### Added — `[Description]` and `[DefaultValue]` coverage
    
    - `[DefaultValue("-")]` added to `ApplySettings.File`.
    - `[DefaultValue(".")]` added to `PackSettings.Path`.
    - `[Description]` added to `ConfigSetSettings` key and value arguments (were undocumented).
    - `[Description]` added to `LogSettings` `--session` and `--root` options (were undocumented).
    - `[Description]` added to `SnapDiffSettings` `<from>` and `<to>` arguments (were undocumented).
    - `[Description]` added to `SessionNewSettings` `--artifacts-dir` option (was undocumented).
    - All existing descriptions reviewed and reworded for consistency and accuracy.
    
    ### Added — XML documentation coverage
    
    Complete XML documentation (`<summary>`, `<remarks>`, `<param>`, `<returns>`, `<exception>`) added to every previously undocumented public, protected, and internal member across all 28 source files:
    
    **`Dx.Cli`**
    - `ApplySettings`, `ApplyCommand`, `RenderApplyResult`, `RenderDocumentSummary`
    - `ConfigBaseSettings`, `ConfigScope`, `ConfigGetSettings`, `ConfigGetCommand`, `ConfigSetSettings`, `ConfigSetCommand`, `ConfigUnsetSettings`, `ConfigUnsetCommand`, `ConfigListSettings`, `ConfigListCommand`, `ConfigShowEffectiveSettings`, `ConfigShowEffectiveCommand`
    - `DxCommandBase<TSettings>`, `FindRoot`, `HandleDxException`, `HandleUnexpected`
    - `EvalSettings`, `EvalCommand`, `RunTimed`, `ExitMarkup`, `FirstLine`
    - `InitSettings`, `InitCommand`
    - `LogSettings`, `LogCommand`
    - `PackSettings`, `PackCommand`, `IsExcluded`, `ReadUtf8Async`, `AppendTree`
    - `RunSettings`, `RunCommand`, `ExecuteAsync`
    - `SessionListSettings`, `SessionListCommand`, `ResolveHeadHandle`, `SessionRow`
    - `SessionNewSettings`, `SessionNewCommand`
    - `SessionShowSettings`, `SessionShowCommand`
    - `SessionCloseSettings`, `SessionCloseCommand`
    - `SnapBaseSettings`, `SnapListSettings`, `SnapListCommand`
    - `SnapShowSettings`, `SnapShowCommand`
    - `SnapDiffSettings`, `SnapDiffCommand`
    - `SnapCheckoutSettings`, `SnapCheckoutCommand`
    - `DxCliErrorRenderer`, `RenderParseError`, `RenderRuntimeError`
    
    **`Dx.Core`**
    - `BlobStore`, `InsertFile`, `OpenRead`, `ReadAllBytes`
    - `DxDatabase`, `UtcNow`, `Open`, `ApplyPragmas`, `Migrate`; `Migration` record; `Migrations` class and `V1Schema`
    - `DxError` enum (all twelve values individually documented); `DxException`, `Error`, `IsRecoverable`, `ExitCode`
    - `DxHash`, `Sha256File`, `Sha256Bytes`, `ToHex`, `FromHex`, `Equal`
    - `DxLock`, `Acquire`, `Dispose`
    - `DxPath`, `Normalize`, `ToAbsolute`, `IsUnderRoot`, `AsDirectoryPrefix`
    - `DxRuntime` primary constructor params; `Open`, `Init`, `ApplyAsync`, `ListSnaps`, `GetHead`, `GetSnapFiles`, `Diff`, `Checkout`, `GetLog`, `MaterializeSnapAsync`, `ResolveHandle`, `GetCurrentHeadHash`
    - `SnapInfo` (both constructors, all properties); `SnapFileInfo`; `LogEntry` (both constructors, all properties); `DiffStatus` (all four values); `DiffEntry`; `SnapMaterializeRow`
    - `SnapHandleRow`; `HandleAssigner`, `AssignHandle`, `Resolve`, `ReverseResolve`, `ListOrdered`
    - `IDxLogger`, `Info`, `Warn`, `Error`, `Debug`; `NullDxLogger`; `ConsoleDxLogger`
    - `IgnoreSet`, `Build`, `IsExcluded`, `Serialize`, `Deserialize`, `NormalizeDeclared`
    - `ManifestEntry` record (all four parameters); `ManifestBuilder`, `Build`, `ComputeSnapHash`
    - `FileManifestRow`; `RollbackEngine` primary constructor params; `RestoreTo`, `LoadManifest`, `PruneEmptyDirs`
    - `SnapshotWriter` primary constructor; `Persist`
    - `OperationResult`, `DispatchResult`; `DxDispatcher` primary constructor params; `DispatchAsync`, `DispatchMutationBlock`, `DispatchReadOnlyBlock`, `WriteFile`, `ApplyPatch`, `ExecuteFsOp`, `ExecuteRunAsync`, `GetShell`, `BeginPending`, `ClearPending`, `RecoverIfNeeded`, `AppendLog`, `GetCurrentHead`, `ResolveAndValidate`, `IsMutation`, `ResolveEncoding`, `ResolveEncodingWithBom`, `DetectEncoding`, `StripBom`, `NormalizeLineEndings`
    - `DxHeader`, `DxBlock`, `FileBlock`, `PatchHunk`, `PatchBlock`, `FsBlock`, `RequestBlock`, `ResultBlock`, `SnapBlock`, `NoteBlock`, `DxDocument`, `IsMutating`, `ParseError`
    - `DxParser`, `ParseText`, `ParseFileAsync`, `Parse`, `ParseHeader`, `ReadBody`, `ReadResultBody`, `ParseHunks`, `ParseArgs`, `StripOneIndent`, `IsComment`
    - `PatchEngine`, `Apply`, `ApplyHunk`, `ApplyReplace`, `ApplyInsert`, `ApplyDelete`, `SplitLines`, `ParseLineRange`, `ParseSingleLine`, `UnquotePattern`, `FindFirst`, `ValidateRange`, `ValidateLine`
    
    ### Changed — code quality and consistency
    
    - `DiffStatus` enum expanded from a single-line declaration to a multi-line form with per-value XML documentation.
    - `ManifestEntry` record: inline field comments replaced by proper XML `<param>` documentation.
    - `DxIR.cs` record field inline comments (e.g. `// normalized, relative, forward-slash`) replaced by XML `<param>` docs on all record types.
    - `DxParser`, `DxDispatcher`, `PatchEngine`: `switch` case indentation standardised to consistent 8-space body alignment.
    - `DxDispatcher.ApplyReplace` (All branch): redundant intermediate variable removed; direct `return` used.
    - `RollbackEngine.RestoreTo`: pass comments labelled `Pass 1`, `Pass 2`, `Pass 3` for clarity; `File.Create`/`OpenWrite` explanatory comment repositioned above the relevant code.
    - SQL string literals in `DxRuntime.Init` reformatted to consistent 16-space indentation.
    - Whitespace alignment normalised throughout `DxDispatcher` (constructor params, `OperationResult`, `DispatchResult`, switch expressions).
    - Redundant inline comments removed from `DxRuntime.Init` (`# Register the session`, `# Build Genesis Manifest`, etc.).
    - `SnapshotWriter` step comment: `4. T-handle (optimistic retry...)` → `4. T-handle assignment (optimistic retry...)`.
    - `ManifestBuilder.ComputeSnapHash` null-separator comment clarified: `[0x00]; // null separator ensures path boundaries are distinct`.
    - All section-divider comments (`// ── Name ───...`) standardised to a uniform 80-character width across all files.
    
    ### Added — solution structure
    
    - `docs/` solution folder added to `Dx.Cli.sln.slnx`, containing `CHANGELOG.md`, `CLI.md`, `CONFIGURATION.md`, `CONTRIBUTING.md`, `TROUBLESHOOTING.md`, and `README.md`.
    
    ### Added — documentation files
    
    Five new documentation files created under `docs/`:
    
    - `docs/CLI.md` — Complete command reference for all commands and sub-commands, with argument tables, options tables (including all short flags), behavior descriptions, and exit code tables.
    - `docs/CONFIGURATION.md` — Full reference for all nine configuration keys (`session.require_base`, `run.run_timeout`, `run.allowed_commands`, `conflict.on_base_mismatch`, `snap.exclude`, `snap.include_build_output`, `encoding.default_encoding`, `encoding.default_line_endings`, `git.record_git_sha`), with valid values, defaults, scope constraints, and usage examples.
    - `docs/CONTRIBUTING.md` — Contributing guide covering principles, workflow, requirements, code style rules (XML docs, short flags, `[Description]`, `[DefaultValue]`), and sister-tool compatibility conventions (`snap.db`, `snaps.lock`).
    - `docs/TROUBLESHOOTING.md` — Troubleshooting guide covering all exit codes (including `3` and `124`), all common failure scenarios, crash recovery, and the three structured error codes.
    - `docs/CHANGELOG.md` — This file.
    
    ### Changed — `README.md`
    
    Replaced the single-line placeholder `# Dx.Cli` with a complete README covering installation (WinGet and manual), quick-start examples, core concepts (workspace, transaction, snapshot, session), documentation links, and design principles.
    
    ---
    
    ## [0.1.0] - Initial Release
    
    ### Added
    
    - Transactional `apply` command
    - Snapshot system (`snap list`, `snap show`)
    - Execution command (`run`)
    - Differential execution (`eval`)
    - Configuration system with precedence resolution
    - Workspace initialization (`init`)
    - Packaging (`pack`)
    - Session logging (`log`)
    

