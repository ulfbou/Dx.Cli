# Command-Line Interface

## Syntax

```
dxs <command> [arguments] [options]
```

All options may be placed after the subcommand in any order.
Short flags are available for frequently used options and are listed alongside their long forms.

---

## Global options

The following options are accepted by most commands.

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. Defaults to the nearest ancestor directory containing a `.dx/` folder. |
| `--session <id>` | `-s` | Target a specific session. Defaults to the most recently created open session. |

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

### Errors

- Fails with exit code `1` if a `.dx/` directory already exists at or above the target path.

---

## apply

Applies a DX document to the workspace as an atomic transaction.

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
| `--dry-run` | `-n` | Parse and validate the document without applying any changes or creating a snapshot. |
| `--verbose` | `-v` | Emit detailed diagnostic output to stderr. |

### Behavior

- Parses the document and validates all operations before any changes are made.
- Checks that the document's `base=` handle matches the current HEAD (when present). Exits with code `3` on mismatch.
- Executes all mutation blocks (`%%FILE`, `%%PATCH`, `%%FS`) atomically, then evaluates any `%%REQUEST type="run"` gates.
- On success: commits all changes and produces a new snapshot. Prints the new handle (e.g. `T0004`) to stdout.
- On failure: rolls the working tree back to its pre-transaction state. No snapshot is created.
- Documents with no mutations (read-only) skip the transaction lifecycle entirely.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Transaction committed successfully. |
| `1` | Transaction failed; working tree rolled back. |
| `2` | Parse or validation failure; no changes made. |
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
- When no differences are found, prints `No differences.` and exits with code `0`.

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
- Records the resulting tree as a new snapshot in the session (or reuses the existing handle if the content is identical).
- Acquires the workspace lock for the duration of the operation.

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
- Binary files and files in excluded directories (`.dx/`, `bin/`, `obj/`, `.git/`, `node_modules/`, `.vs/`, `.vscode/`, `.idea/`, `.github/`, `.hg/`, `.svn/`) are silently skipped.
- Output is always UTF-8 without BOM, regardless of the source file encoding.
- When writing to a file (`--out`), prints a summary of packed and skipped file counts.

---

## run

Executes a shell command against the current working tree or an isolated snapshot.

### Usage

```
dxs run <command> [options]
```

### Arguments

| Argument | Description |
|---|---|
| `<command>` | Shell command to execute. Use `--` to separate `dxs` options from commands that begin with a dash. |

### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |
| `--session <id>` | `-s` | Target session identifier. |
| `--snap <handle>` | | Run against this snapshot in isolation. Working tree is unchanged. Defaults to HEAD. |
| `--timeout <seconds>` | `-t` | Command timeout in seconds. `0` = no timeout (default). |
| `--artifacts-dir <path>` | `-a` | Directory for command output artifacts (excluded from snaps). |

### Behavior

- When `--snap` is specified, the snapshot is materialised into a temporary directory, the command runs there, and the directory is deleted on exit regardless of outcome. The working tree is never modified.
- When `--snap` is omitted, the command runs directly in the workspace root against the current working tree.
- Combined stdout and stderr of the child process are forwarded to the caller's stdout.
- The command's exit code becomes the exit code of `dxs run`.
- When the timeout expires, the process tree is killed and exit code `124` is returned.

### Errors

- Exit code `2` when the specified `--snap` handle does not exist.

---

## eval

Materialises two snapshots into isolated directories, executes a command against each, and evaluates the results against a pass condition.

### Usage

```
dxs eval <snap-a> <snap-b> <command> [options]
```

### Arguments

| Argument | Description |
|---|---|
| `<snap-a>` | Baseline snapshot handle (e.g. `T0000`). |
| `<snap-b>` | Candidate snapshot handle (e.g. `T0005`). |
| `<command>` | Shell command to run against both snapshots. |

### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |
| `--session <id>` | `-s` | Target session identifier. |
| `--timeout <seconds>` | `-t` | Per-snapshot command timeout in seconds. `0` = no timeout (default). |
| `--sequential` | | Materialise and execute snapshots one after the other instead of concurrently. |
| `--pass-if <expr>` | `-p` | Pass condition (see table below). Default: `b-passes`. |
| `--label-a <label>` | | Display label for `snap-a` in the results table. Defaults to the snap handle. |
| `--label-b <label>` | | Display label for `snap-b` in the results table. Defaults to the snap handle. |
| `--artifacts-dir <path>` | | Directory for command output artifacts (excluded from snaps). |

### Pass conditions

| Expression | Passes when |
|---|---|
| `b-passes` | Candidate (`snap-b`) exit code is `0`. |
| `exit-equal` | Both exit codes are identical. |
| `both-pass` | Both exit codes are `0`. |
| `no-regression` | Candidate exit code ≤ baseline exit code. |

### Behavior

- Both snapshots are materialised and executed concurrently by default. Use `--sequential` when the command requires exclusive filesystem or port access.
- Temporary directories are deleted after the command completes regardless of outcome.
- Prints a results table showing exit code and first output line for each snapshot, followed by `PASS` or `FAIL`.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Pass condition satisfied. |
| `1` | Pass condition not satisfied, or a `DxException` was thrown. |
| `2` | Invalid `--pass-if` expression. |

---

## config

Reads and writes configuration values at global or local (workspace) scope.

### Usage

```
dxs config <command> [options]
```

### Common options

| Option | Short | Description |
|---|---|---|
| `--global` | `-g` | Target the global config store (`~/.dx/.dx/snap.db`). |
| `--local` | `-l` | Target the local workspace config store (`<root>/.dx/snap.db`). Default. |
| `--root <path>` | `-r` | Override workspace root. |

---

### config get

Retrieves a single configuration value.

```
dxs config get <key> [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `<key>` | Configuration key to read, e.g. `conflict.on_base_mismatch`. |

#### Behavior

- When the key has no explicitly stored value, the built-in default is printed with a `(default)` suffix.
- When the key is unrecognised, an error is reported.

---

### config set

Creates or updates a configuration value at the active scope.

```
dxs config set <key> <value> [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `<key>` | Configuration key to set. |
| `<value>` | Value to assign. All values are stored as strings. |

#### Errors

- Exit code `2` for an unknown key or an attempt to set a local-only key (`snap.exclude`) at global scope.

---

### config unset

Removes a stored configuration value from the active scope.

```
dxs config unset <key> [options]
```

#### Arguments

| Argument | Description |
|---|---|
| `<key>` | Configuration key to remove. |

#### Behavior

- If the key has no stored value at the target scope, an informational message is printed and the command exits with code `0`.
- After removal, subsequent reads fall back to any wider scope or the built-in default.

---

### config list

Lists all explicitly stored configuration values at the active scope.

```
dxs config list [options]
```

#### Behavior

- Only explicitly set values are shown. Built-in defaults that have not been overridden do not appear.
- Use `dxs config show-effective` to see the full resolved configuration including defaults.

---

### config show-effective

Displays the fully resolved configuration after applying all precedence rules.

```
dxs config show-effective [options]
```

#### Options

| Option | Short | Description |
|---|---|---|
| `--root <path>` | `-r` | Override workspace root. |
| `--session <id>` | `-s` | Session identifier (informational only). |

#### Behavior

- Merges global, local, and built-in default values and displays each key with its effective value and source (`local`, `global`, or `default`).
- Precedence order (highest to lowest): command-line → session → local → global → built-in default.
