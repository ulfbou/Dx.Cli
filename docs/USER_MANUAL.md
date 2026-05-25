# `dxs` User Manual

`dxs` is a transactional CLI for deterministic workspace mutation, snapshotting, and differential execution. 

### Common Options
Many commands accept these standard options:
* `-r, --root <path>`: Override the workspace root (defaults to the nearest ancestor with a `.dx/` folder).
* `-s, --session <id>`: Target a specific session identifier (defaults to the most recent active session).
* `-v, --verbose`: Emit detailed diagnostic output to `stderr`.
* `--no-color`: Disable ANSI colors.

---

## Core Commands

### `init`
Initializes a new DX workspace and creates the genesis snapshot (`T0000`).
* **Usage**: `dxs init [path]` *(Defaults to current directory)*
* **Options**:
  * `-s, --session <id>`: ID for the genesis session (defaults to UTC timestamp).
  * `-a, --artifacts-dir <path>`: Directory excluded from all snaps (e.g., CI output).
  * `-x, --exclude <paths>`: Comma-separated additional paths to exclude.
  * `-b, --include-build-output`: Include `bin/` and `obj/` directories (excluded by default).
  * `-v, --verbose`: Enable verbose output.

### `apply`
Hands the raw DX document to `DxDispatcher`, which parses, validates, and executes it as an atomic transaction. The CLI performs no parsing.
* **Usage**: `dxs apply [file]` *(Defaults to `-` for stdin)*
* **Options**:
  * `-r, --root <path>`, `-s, --session <id>`, `-v, --verbose`
  * `-n, --dry-run`: Parse and validate without applying changes.
  * `--on-base-mismatch <reject|warn>`: Override base-mismatch behavior for this run.
  * `--timeout <seconds>`: Timeout for run gate blocks (0 = no timeout).

### `run`
Executes a shell command against the working tree or an isolated materialized snapshot.
* **Usage**: `dxs run <command>` *(Use `--` to separate dxs options from your command)*
* **Options**:
  * `-r, --root <path>`, `-s, --session <id>`
  * `--snap <handle>`: Run against a specific snap state in isolation (working tree unchanged).
  * `-t, --timeout <seconds>`: Command timeout (0 = no timeout).
  * `-a, --artifacts-dir <path>`: Directory for command output artifacts (excluded from snaps).

### `eval`
Materializes two snapshots into isolated directories and compares the result of running a command against each.
* **Usage**: `dxs eval <snap-a> <snap-b> <command>`
* **Options**:
  * `-r, --root <path>`, `-s, --session <id>`, `-t, --timeout <seconds>`
  * `--sequential`: Run sequentially instead of concurrently.
  * `-p, --pass-if <expr>`: Condition for a 0 exit code: `b-passes` (default), `exit-equal`, `both-pass`, or `no-regression`.
  * `--label-a <label>`, `--label-b <label>`: Display labels for the results table.
  * `--artifacts-dir <path>`: Directory for command output artifacts.

### `pack`
Serializes workspace files into a read-only DX document suitable for LLM context.
* **Usage**: `dxs pack <path>` *(Defaults to `.`)*
* **Options**:
  * `-r, --root <path>`, `-s, --session <id>`
  * `-o, --out <file>`: Output file path (omit to write to stdout).
  * `--session-header`: Prepend a `%%DX` session header.
  * `--tree`: Prepend a directory tree overview as a `%%NOTE` block.
  * `-f, --file-type <ext>`: Filter by file extension (e.g., `.cs`).
  * `--lines <spec>`: Include only a specific line range (format: `relative/path:N-M`).
  * `-m, --metadata`: Emit a `%%NOTE` metadata block before each file.

### `log`
Shows a reverse-chronological history of transaction events.
*Since v0.3.0, every execution (including parse failures and dry-runs) produces exactly one intent log entry and one result log entry — the dual-log invariant.*
* **Usage**: `dxs log`
* **Options**:
  * `-r, --root <path>`, `-s, --session <id>`
  * `-n, --limit <count>`: Max entries to display (default: 20).

### `doctor`
Inspects the workspace for known problem states (stale locks, stuck transactions, oversized WAL) and optionally repairs them.
* **Usage**: `dxs doctor`
* **Options**:
  * `-r, --root <path>`
  * `--repair`: Attempt to automatically repair detected issues.

---

## Entity Management Commands

### `snap`
Manages snapshots within a session.
* **`list`**: Renders the chronological snapshot graph. *(Options: `-r`, `-s`)*
* **`show <handle>`**: Displays snap metadata. *(Options: `-r`, `-s`, `-f|--files` to list all tracked files)*
* **`diff <from> <to>`**: Shows file-level differences. *(Options: `-r`, `-s`, `-p|--path <filter>` to scope to a prefix)*
* **`checkout <handle>`**: Restores the working tree to the snap state and records it as a new snapshot. *(Options: `-r`, `-s`)*

### `session`
Manages workspace sessions.
* **`list`**: Displays all sessions, their HEAD, and status. *(Options: `-r`)*
* **`new [id]`**: Starts a new session from the current tree. *(Options: `-r`, `-a|--artifacts-dir`, `-x|--exclude`, `-b|--include-build-output`)*
* **`show [id]`**: Shows HEAD, snap count, and recent activity. *(Options: `-r`)*
* **`close [id]`**: Marks a session as closed (retains all data but removes it as the default active session). *(Options: `-r`)*

### `config`
Reads and writes configuration values.
* **Scopes**: `-g, --global` (`~/.dx/snap.db`), `-l, --local` (`<root>/.dx/snap.db` - default).
* **`get <key>`**: Gets a value. *(Options: `-g`, `-l`, `-r`)*
* **`set <key> <value>`**: Sets a value. *(Options: `-g`, `-l`, `-r`)*
* **`unset <key>`**: Removes a value. *(Options: `-g`, `-l`, `-r`)*
* **`list`**: Lists all explicitly set values at the active scope. *(Options: `-g`, `-l`, `-r`)*
* **`show-effective`**: Shows merged config across all scopes/defaults. *(Options: `-r`, `-s`)*
