# Configuration

## Scopes

Configuration is resolved through four scopes, applied in precedence order from highest to lowest.

| Scope | Description |
|---|---|
| Command-line | Flags passed directly on the command line. Always wins. |
| Session | Values set for a specific session (reserved for future use). |
| Workspace | Values stored in the local workspace database. |
| Global | Values stored in the user's global database. |

## Precedence

```
command-line  >  session  >  workspace  >  global  >  built-in default
```

## Storage

| Scope | Path |
|---|---|
| Workspace | `<workspace>/.dx/snap.db` |
| Global | `~/.dx/.dx/snap.db` |

Configuration values are stored as strings in the `config` table and interpreted at runtime.

---

## Keys

### session.require_base

Controls whether a `base=` handle is required in DX documents.

| Value | Behavior |
|---|---|
| `require` | Reject documents that do not include a `base=` handle. |
| `warn` | Accept documents without `base=` but emit a warning. |
| `ignore` | Accept documents without `base=` silently. |

**Default:** `warn`

---

### run.run_timeout

Maximum execution time for commands launched via `dxs run` and `dxs eval`, in milliseconds.

| Value | Behavior |
|---|---|
| `0` | No timeout — the command runs until it completes naturally. |
| positive integer | Kill the process tree after the specified number of milliseconds. |

**Default:** `0`

---

### run.allowed_commands

A JSON array of command prefix strings that are permitted to execute via `%%REQUEST type="run"` gates and `dxs run`. An empty array (`[]`) means all commands are allowed.

**Default:** `[]`

---

### conflict.on_base_mismatch

Behavior when the `base=` handle in a DX document does not match the current HEAD snapshot.

| Value | Behavior |
|---|---|
| `reject` | Abort the transaction immediately (exit code `3`). |
| `warn` | Emit a warning and continue applying the document. |
| `allow` | Apply the document silently regardless of the mismatch. |

**Default:** `reject`

---

### snap.exclude

A JSON array of path prefixes (relative to the workspace root) that are excluded from all snapshots in this workspace.

**Scope:** Workspace only — cannot be set globally, as doing so would make snapshot hashes non-portable across machines.

**Notes:**
- `.dx/` is always excluded and cannot be overridden.
- Built-in exclusions (`.git/`, `.hg/`, `.svn/`, `node_modules/`, `.vs/`, `.idea/`) are always applied regardless of this setting.

**Default:** `[]`

---

### snap.include_build_output

Controls whether `bin/` and `obj/` directories are included in snapshots.

| Value | Behavior |
|---|---|
| `false` | Exclude `bin/` and `obj/` from all snapshots (default). |
| `true` | Include `bin/` and `obj/` in snapshots. |

**Default:** `false`

---

### encoding.default_encoding

The default character encoding applied when writing files via `%%FILE` blocks that do not specify an explicit `encoding=` attribute.

Supported values: `utf-8`, `utf-8-no-bom`, `utf-8-bom`, `utf-16-le`, `utf-16-be`, `ascii`, `latin-1`.

**Default:** `utf-8`

---

### encoding.default_line_endings

The default line-ending normalisation applied when writing files via `%%FILE` blocks.

| Value | Behavior |
|---|---|
| `preserve` | Keep whatever line endings are present in the document body. |
| `lf` | Normalise all line endings to `\n`. |
| `crlf` | Normalise all line endings to `\r\n`. |
| `cr` | Normalise all line endings to `\r`. |

**Default:** `preserve`

---

### git.record_git_sha

Controls whether the current Git HEAD SHA is recorded alongside each snapshot.

| Value | Behavior |
|---|---|
| `true` | Record the Git SHA when taking a snapshot (default). |
| `false` | Skip Git SHA recording. |

**Default:** `true`

---

## Examples

Set the base-mismatch behavior to `warn` for the current workspace:

```
dxs config set conflict.on_base_mismatch warn
```

Set a global run timeout of 30 seconds (30,000 ms):

```
dxs config set --global run.run_timeout 30000
```

Exclude an additional directory from all snapshots:

```
dxs config set snap.exclude '["artifacts/","coverage/"]'
```

Show all effective values after applying precedence rules:

```
dxs config show-effective
```

View a single value, falling back to the built-in default if not set:

```
dxs config get run.run_timeout
```
