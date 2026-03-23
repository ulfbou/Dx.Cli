# Changelog

## [0.2.0] - 2026-03-23

### Renamed — `dx` → `dxs`

The binary, tool command name, assembly name, and product name have all been renamed
from `dx` to `dxs` to avoid conflicts with existing `dx` binaries on the user's PATH.

- `AssemblyName` changed from `dx` to `dxs`.
- `Product` changed from `dx` to `dxs`.
- `PackageProjectUrl` updated to `https://github.com/ulfbou/dx.cli`.
- `PackageId`, `PackAsTool`, and `ToolCommandName` added to `Dx.Cli.csproj` to publish
  as a `dotnet tool` (`dxs`).
- `config.SetApplicationName("dx")` → `config.SetApplicationName("dxs")` in `Program.cs`.
- All user-facing error messages and command-listing strings in `DxCliErrorRenderer`
  updated from `dx <subcommand>` to `dxs <subcommand>`.
- Runtime error messages `"Run 'dx init' first."` → `"Run 'dxs init' first."` in
  `DxRuntime` and `SessionCommands`.
- Session log `document` column entry for workspace init changed from `'dx init'` to
  `'dxs init'`.

### Fixed — database and lock filenames

- `DxDatabase.Open` previously hardcoded the database filename as `dx.db`. It now
  accepts a `dbName` parameter defaulting to `snap.db`. All call sites (workspace and
  global config) now use `snap.db`, matching the schema definition.
- `DxLock.Acquire` previously created a lock file named `.dx/lock`. It now accepts a
  `lockName` parameter defaulting to `snaps.lock`, scoping the lock to snapshot
  operations and avoiding conflicts with sister tools that share the `.dx/` directory.
- Lock error message now includes the lock filename:
  `"Session is locked by another process ({lockName})."`.
- `ConfigCommands.ConfigBaseSettings` `--global` description corrected from
  `~/.dx/config.db` to `~/.dx/snap.db`.
- `ConfigCommands.ConfigBaseSettings` `--local` description corrected from
  `<root>/.dx/dx.db` to `<root>/.dx/snap.db`.
- `ConfigShowEffectiveCommand` local-database existence check corrected from `dx.db`
  to `snap.db`.
- `SessionListCommand` and `SessionNewCommand` workspace existence checks corrected
  from `dx.db` to `snap.db`.
- `PackCommand.IsExcluded` internal comment corrected from `subfolder/.dx/dx.db` to
  `subfolder/.dx/snap.db`.

### Fixed — bugs introduced in rc1

- **Bug 1.1:** `EvalCommand` called `MaterializeSnapAsync` and `RunTimed` twice each
  per invocation — once inside the `Status` lambda and once unconditionally outside it.
  The duplicate outer calls created a second pair of temp directories that were never
  cleaned up. Both duplicates removed; locals pre-initialised to `string.Empty`/`default`
  so the compiler is satisfied without `null!`.
- **Bug 1.2:** `SnapDiffCommand` zero-diff message `"No differences."` normalised to
  `"No differences found."` for stable matching across TTY and non-TTY environments.
- **Bug 1.3:** `SnapCheckoutCommand.ExecuteAsync` used the synchronous
  `AnsiConsole.Status().Start()` overload inside an `async Task<int>` method. Switched
  to `StartAsync`; `newHandle` initialised to `string.Empty`; catch clauses updated to
  return plain `int` (not `Task.FromResult`).
- **Bug 1.4:** `DxDispatcher.AppendLog` wrote `doc.Header.Author ?? "llm"` directly to
  the `direction` column, which has a `CHECK (direction IN ('llm','tool'))` constraint.
  Any author value other than `"llm"` or `"tool"` threw a SQLite constraint violation at
  commit time — after mutations were already applied. Normalised:
  `doc.Header.Author?.ToLowerInvariant() == "tool" ? "tool" : "llm"`.
- **Bug 1.5:** `DxDatabase.Open(root)` appends `.dx/snap.db` to `root`; passing `~/.dx`
  therefore produced `~/.dx/.dx/snap.db`. Added `DxDatabase.OpenGlobal()` which opens
  `~/.dx/snap.db` directly. `ConfigScope.OpenDb` and `ConfigShowEffectiveCommand` both
  updated to use it.
- **Bug 1.6:** `DxDispatcher.BeginPending` used a plain `INSERT INTO pending_transaction`.
  If `RecoverIfNeeded` crashed before deleting a stale row, the next `BeginPending` call
  threw a unique-constraint violation, permanently locking the workspace. Changed to
  `INSERT OR REPLACE`.

### Changed — `dxs log` default limit

- `--limit` default reduced from `50` to `20`. The `[DefaultValue(20)]` attribute is now
  used exclusively; the field initialiser `= 50` has been removed.

### Added — short flags on all command options

All `[CommandOption]` entries that lacked a single-character short flag have been given
one. The complete set added across all command settings classes:

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

Complete XML documentation (`<summary>`, `<remarks>`, `<param>`, `<returns>`,
`<exception>`) added to every previously undocumented public, protected, and internal
member across all 28 source files.

### Added — solution structure

- `docs/` solution folder added to `Dx.Cli.sln.slnx`, containing `CHANGELOG.md`,
  `CLI.md`, `CONFIGURATION.md`, `CONTRIBUTING.md`, `TROUBLESHOOTING.md`, and `README.md`.

### Added — documentation files

Five new documentation files created under `docs/`:

- `docs/CLI.md` — Complete command reference for all commands and sub-commands, with
  argument tables, options tables (including all short flags), behavior descriptions,
  and exit code tables.
- `docs/CONFIGURATION.md` — Full reference for all nine configuration keys, with valid
  values, defaults, scope constraints, and usage examples.
- `docs/CONTRIBUTING.md` — Contributing guide covering principles, workflow,
  requirements, code style rules, and sister-tool compatibility conventions
  (`snap.db`, `snaps.lock`).
- `docs/TROUBLESHOOTING.md` — Troubleshooting guide covering all exit codes (including
  `3` and `124`), all common failure scenarios, and crash recovery. Structured error
  codes are deferred to a future release.
- `docs/CHANGELOG.md` — This file.

### Added — cross-platform distribution

`dxs` v0.2.0 ships pre-built self-contained binaries for six platforms:
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

Four installation channels are supported:

| Channel | Command |
|---|---|
| Windows (WinGet) | `winget install ulfbou.dxs` |
| macOS (Homebrew) | `brew install ulfbou/tap/dxs` |
| Linux / macOS (script) | `curl -sSL https://raw.githubusercontent.com/ulfbou/dx.cli/main/install.sh \| bash` |
| Any platform (.NET 10) | `dotnet tool install -g dxs` |

- `install.sh` — added to repo root. Detects OS and architecture, fetches the correct
  release asset, and installs to `/usr/local/bin`. Handles the `aarch64` → `arm64` RID
  mapping, uses a cleanup trap, and conditionally uses `sudo` only when the install
  directory is not user-writable.
- `Formula/dxs.rb` — initial Homebrew formula committed to `ulfbou/homebrew-tap`.
  Targets `osx-arm64`. Intel Mac (`osx-x64`) support will be added in a future release.
- Release pipeline updated to build all six RIDs, produce `.zip` for Windows and
  `.tar.gz` for Linux/macOS (preserving executable permissions), push to NuGet, and
  automatically update the Homebrew tap formula on stable releases.

> **macOS Gatekeeper:** Unsigned binaries installed via `install.sh` will trigger
> the "Developer cannot be verified" prompt on first run. Run
> `xattr -d com.apple.quarantine /usr/local/bin/dxs` to clear it. Binaries installed
> via Homebrew or `dotnet tool install` are not affected.

### Added — vNext state model scaffold

A no-op environment variable check is present in `Program.cs`:

```
DX_VNEXT_STATE_MODEL=1 dxs <command>
```

Setting this variable emits a warning that vNext is not yet active. This gives the
migration milestone a flag to flip without a flag-day change.

### Known Limitations

- Handle assignment under concurrent writes can be non-deterministic in pathological
  multi-process scenarios where two processes race to insert the next `seq` value into
  `snap_handles`. The `UNIQUE(session_id, seq)` constraint plus the optimistic retry
  loop in `HandleAssigner` mitigates this in practice, but it is not formally
  eliminated. This will be resolved as part of the vNext state model migration
  (log-canonical handle derivation).
- The Homebrew formula targets `osx-arm64` only. Intel Mac users should use
  `install.sh` or `dotnet tool install -g dxs` until multi-arch formula support is
  added.
- macOS binaries are unsigned and unnotarized. Gatekeeper will prompt on first run
  for binaries installed via `install.sh`. See above for the workaround.

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

