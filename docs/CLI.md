# Command-Line Interface

## Syntax

```
dxs <command> [arguments] [options]
```

All options may be placed after the subcommand in any order. Short flags are available for frequently used options and are listed alongside their long forms.

---

## Global options

The following options are accepted by all commands.

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. Defaults to the nearest ancestor directory containing a `.dx/` folder. |
| `--session <id>` | `-s` | Target a specific session. Defaults to the most recently created open session. |
| `--no-color` | | Disable ANSI color codes in output. Useful when piping to files or non-TTY environments. |
| `--verbose` | `-v` | Emit detailed diagnostic output to stderr (where supported by the command). |

Output behavior: when stdout is redirected, `dxs` automatically reroutes progress and diagnostic output to stderr to keep piped data clean.

---

## init

Initialises a new DX workspace and takes the genesis snapshot `T0000`.

### Usage

```
dxs init [path] [options]
```

### Arguments

| Argument | Description |
|---|---|
| `[path]` | Directory to initialise. Defaults to the current directory. |

### Options

| Option | Short | Description |
|---|---|---|
| `--session <id>` | `-s` | Session identifier for the genesis session. Defaults to `session-yyyyMMdd-HHmmss`. |
| `--artifacts-dir <path>` | `-a` | Directory excluded from all snaps (e.g. a CI artifact output directory). |
| `--exclude <paths>` | `-x` | Comma-separated additional paths to exclude from snaps. |
| `--include-build-output` | `-b` | Include `bin/` and `obj/` directories in snaps. Excluded by default. |
| `--verbose` | `-v` | Emit detailed diagnostic output to stderr. |

### Behavior

- Walks up the directory tree from `path`; fails if a `.dx/` folder already exists at or above the target, preventing nested workspaces.
- Creates a `.dx/` directory containing the workspace database (`snap.db`).
- Registers the genesis session and takes an initial snapshot of the working tree, assigned handle `T0000`.
- Built-in exclusions (`.dx/`, `.git/`, `.hg/`, `.svn/`, `node_modules/`, `.vs/`, `.idea/`) are always applied and cannot be overridden.
- Writes a session_log entry for T0000 (genesis), satisfying the logging invariant.

### Errors

- Fails with exit code `1` if a `.dx/` directory already exists at or above the target path.

---

## apply

Applies a DX document to the workspace as an atomic transaction. **All parsing, validation, and execution is owned by `DxDispatcher` — the CLI is a thin transport layer.**

### Usage

```
dxs apply [file] [options]
```

### Arguments

| Argument | Description |
|---|---|
| `[file]` | Path to a `.dx` document. Pass `-` or omit to read from stdin. Defaults to `-`. |

### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |
| `--session <id>` | `-s` | Target session identifier. |
| `--dry-run` | `-n` | Parse and validate the document without applying any changes or creating a snapshot. Still writes intent and result log entries. |
| `--on-base-mismatch <reject|warn>` | | Per-invocation override for base-mismatch behavior. Overrides `conflict.on_base_mismatch` config. |
| `--timeout <seconds>` | | Per-invocation timeout for `%%REQUEST type="run"` gates. Overrides `run.run_timeout`. `0` = no timeout. |
| `--verbose` | `-v` | Emit detailed diagnostic output to stderr. |

### Behavior

- Reads raw document text and hands it directly to `DxDispatcher` — **no parsing occurs in the CLI**.
- Dispatcher writes an **intent log** entry before any work begins.
- Dispatcher parses the document internally; on parse failure it writes a **result log** entry and returns — CLI never sees the parse exception.
- Base-handle validation occurs inside the transaction boundary in Dispatcher (not in CLI). Exits with code `3` on mismatch when `reject` is active.
- Executes all mutation blocks (`%%FILE`, `%%PATCH`, `%%FS`) atomically, then evaluates any `%%REQUEST type="run"` gates.
- On success: commits all changes and produces a new snapshot. Prints the new handle (e.g. `T0004`) to stdout.
- On failure: rolls the working tree back to its pre-transaction state. No snapshot is created.
- **Read-only documents** (containing only `%%REQUEST`, `%%NOTE`, or readonly `%%FILE`) are routed through Dispatcher in `Request` mode. They **do not** skip the transaction lifecycle — they produce intent and result log entries, but create no snapshot and make no filesystem changes.
- All exit paths (success, parse failure, validation failure, cancellation, exception) produce exactly one intent + one result log entry (dual-log invariant).

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Transaction committed successfully. |
| `1` | Transaction failed; working tree rolled back. |
| `2` | Parse or validation failure; no changes made (normalized by Dispatcher). |
| `3` | Base-snapshot mismatch; no changes made. |

---

## snap

Manages snapshots within a session.

### Usage

```
dxs snap <command> [options]
```

### Common options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |
| `--session <id>` | `-s` | Target session identifier. |

---

### snap list

Lists all snapshots for the current session in chronological order, with the current HEAD annotated.

```
dxs snap list [options]
```

---

### snap show

Displays metadata for a specific snapshot and, optionally, its complete file manifest.

```
dxs snap show <handle> [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `<handle>` | Snap handle to inspect (e.g. `T0003`). |

#### Options

| Option | Short | Description |
|---|---|---|
| `--files` | `-f` | List all files tracked in the snapshot with their sizes. |

#### Errors

- Exit code `2` when the handle does not exist in the current session.

---

### snap diff

Computes the file-level difference between two snapshots.

```
dxs snap diff <from> <to> [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `<from>` | Baseline snap handle (e.g. `T0000`). |
| `<to>` | Candidate snap handle (e.g. `T0005`). |

#### Options

| Option | Short | Description |
|---|---|---|
| `--path <filter>` | `-p` | Scope the diff to paths beginning with this prefix (e.g. `src/`). |

#### Behavior

- Reports each changed file as `added`, `deleted`, or `modified`. Unchanged files are omitted.
- When no differences are found, prints `No differences found.` and exits with code `0`.

---

### snap checkout

Restores the workspace working tree to the state recorded in a specified snapshot.

```
dxs snap checkout <handle> [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `<handle>` | Snap handle to restore the working tree to (e.g. `T0002`). |

#### Behavior

- Restores the working tree to the exact file set and content of the target snapshot.
- Records the resulting tree as a new snapshot in the session with `checkout_of` set to the target handle, and `parent` set to the previous HEAD. Writes a session_log entry for the checkout (satisfying the state-mutation logging invariant).
- If the target content is identical to the current HEAD, the operation is idempotent and reuses the existing handle (no duplicate snapshot).
- Acquires the workspace lock for the duration of the operation. All work is performed through `DxDispatcher` (since v0.3.0, previously via DxRuntime).

---

## session

Manages session lifecycle within a workspace.

### Usage

```
dxs session <command> [options]
```

---

### session list

Lists all sessions registered in the workspace, including their HEAD snapshot and open/closed status.

```
dxs session list [options]
```

#### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |

---

### session new

Registers a new session in the current workspace and takes a genesis snapshot `T0000` of the current working tree.

```
dxs session new [id] [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `[id]` | Session identifier. Defaults to `session-yyyyMMdd-HHmmss`. |

#### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |
| `--artifacts-dir <path>` | `-a` | Directory excluded from all snaps in this session. |
| `--exclude <paths>` | `-x` | Comma-separated additional paths to exclude from snaps. |
| `--include-build-output` | `-b` | Include `bin/` and `obj/` directories in snaps. Excluded by default. |

#### Behavior

- Requires an already-initialised workspace (`dxs init`).
- The new session does not become the default active session automatically. Use `--session <id>` on subsequent commands to target it explicitly.
- Uses the unified genesis architecture (`WorkspaceInitializer` + `SessionGenesisCreator`) — atomic and transactional.

---

### session show

Displays HEAD snapshot, total snapshot count, and recent activity for a session.

```
dxs session show [id] [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `[id]` | Session ID to inspect. Defaults to the most recent active session. |

#### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |

---

### session close

Marks a session as closed so it no longer appears as the default active session.

```
dxs session close [id] [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `[id]` | Session ID to close. Defaults to the most recent active session. |

#### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |

#### Behavior

- Non-destructive: all snapshots and log entries are retained.
- A closed session can still be targeted explicitly via `--session <id>`.

---

## log

Displays the transaction log for the current session in reverse-chronological order.

### Usage

```
dxs log [options]
```

### Options

| Option | Short | Description |
|---|---|---|
| `--session <id>` | `-s` | Show log for a specific session. |
| `--root <path>` | `-r` | Override workspace root. |
| `--limit <count>` | `-n` | Maximum number of entries to display, newest first. Default: `20`. |

### Behavior

- Includes both successful and failed transactions.
- Each entry shows the transaction direction (`llm` or `tool`), the resulting snapshot handle (if any), success status, and timestamp.
- Since v0.3.0, **every** execution path (including parse failures and dry-runs) produces exactly one intent entry and one result entry — the dual-log invariant.

---

## pack

Serialises workspace files into a read-only DX document suitable for use as LLM context.

### Usage

```
dxs pack <path> [options]
```

### Arguments

| Argument | Description |
|---|---|
| `<path>` | File or directory to pack. Defaults to `.` (current directory). |

### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root used for relative path computation. |
| `--out <file>` | `-o` | Output file path. Omit to write to stdout. |
| `--session-header` | | Prepend a `%%DX` header to produce a valid standalone DX document. |
| `--tree` | | Prepend a directory tree overview as a `%%NOTE` block. |
| `--file-type <ext>` | `-f` | Include only files with this extension, e.g. `.cs`. |
| `--lines <spec>` | | Include only the specified line range. Format: `relative/path:N-M`. |
| `--metadata` | `-m` | Emit a `%%NOTE` metadata block (path, size, line count) before each `%%FILE` block. |

### Behavior

- Each eligible file is emitted as a `%%FILE path="..." readonly="true"` block with its content indented by four spaces, followed by `%%ENDBLOCK`.
- Respects the session `IgnoreSet` (same exclusions as snapshots), closing the prior divergence between pack and snapshot.
- Output is deterministic and suitable for diffing.

---

## eval

Evaluates a DX document containing `%%REQUEST` blocks without mutating the workspace.

### Usage

```
dxs eval [file] [options]
```

### Behavior

- Routes through `DxDispatcher` in Request mode — no snapshot is created.
- Useful for testing run gates or read-only queries.

---

## run

Executes a shell command with workspace-aware timeout and allow-list enforcement.

### Usage

```
dxs run <command> [args...] [options]
```

### Options

| Option | Description |
|---|---|
| `--timeout <seconds>` | Override `run.run_timeout` for this invocation. |

---

## doctor

Inspects workspace health and optionally repairs common issues (added in v0.3.0).

### Usage

```
dxs doctor [options]
```

### Options

| Option | Description |
|---|---|
| `--repair` | Attempt automatic repair of stale locks, stuck pending transactions, oversized WAL files, and sessions missing genesis log entries. |
| `--root <path>` | `-r` | Override workspace root. |

### Behavior

- Checks for: stale `.dx/snaps.lock`, orphaned `pending_transaction` rows, missing T0000 log entries, WAL size.
- With `--repair`, uses async file-scoped locking with exponential backoff and `FileOptions.DeleteOnClose` to eliminate stale locks safely.
- All diagnostics are written to stderr; exit code `0` = healthy, `1` = issues found.

---

## config

Gets, sets, and inspects configuration values.

### Usage

```
dxs config <get|set|show-effective> [key] [value] [options]
```

### Behavior

- Workspace config stored in `<workspace>/.dx/snap.db`
- Global config stored in `~/.dx/snap.db`
- See CONFIGURATION.md for full key reference.

---
