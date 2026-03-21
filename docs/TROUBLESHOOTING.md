# Troubleshooting

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Execution failure (transaction rolled back, session error, unexpected error). |
| `2` | Parse or validation failure, invalid argument, or workspace not initialised. |
| `3` | Base-snapshot mismatch — `base=` handle in document does not match current HEAD. |
| `124` | Command timeout (returned by `dxs run` and `dxs eval` when a process is killed). |

---

## Error reporting

All diagnostic output is written to **stderr**. Structured errors follow the format:

```
error: <message>
```

The `--verbose` / `-v` flag (supported by `dxs init` and `dxs apply`) enables additional debug output on stderr without interfering with piped stdout.

---

## Workspace already initialised

**Cause:** A `.dx/` directory exists at or above the target path.

**Resolution:**

- To use an existing workspace, run commands from within it (or pass `--root <path>`).
- To start fresh, remove the existing `.dx/` directory and re-run `dxs init`.

```
rm -rf .dx
dxs init
```

---

## No active session

**Cause:** The workspace has no open session. This can happen if all sessions have been closed or the workspace database is empty.

**Resolution:**

```
dxs session new
```

If the workspace was never initialised:

```
dxs init
```

---

## Base mismatch (exit code 3)

**Cause:** The `base=` handle in the DX document does not match the current HEAD snapshot. Another transaction has advanced the workspace since the document was authored.

**Resolution:**

1. Check the current HEAD:
   ```
   dxs snap list
   ```
2. Re-author the document with the correct `base=` handle, or remove the `base=` line if conflict checking is not required.
3. Alternatively, relax the behavior with configuration:
   ```
   dxs config set conflict.on_base_mismatch warn
   ```

---

## Transaction failed — working tree rolled back

**Cause:** An error occurred during mutation execution — a patch precondition failed, a file was not found, a run gate returned a non-zero exit code, or a filesystem operation was rejected.

**Resolution:**

1. Inspect the error message printed to stderr.
2. Validate the document structure — check that patch patterns exist in the target files, file paths are correct, and run gate commands succeed independently.
3. Re-run with `--dry-run` to validate without making changes:
   ```
   dxs apply --dry-run changes.dx
   ```

---

## Snapshot not found

**Cause:** The specified snapshot handle does not exist in the current session.

**Resolution:**

List all available handles for the current session:

```
dxs snap list
```

If targeting a different session, pass `--session <id>`:

```
dxs snap list --session my-session
```

---

## Database locked

**Cause:** Another `dxs` process is currently holding the workspace lock (`snaps.lock`). Concurrent mutations on the same workspace are not permitted.

**Resolution:**

- Ensure no other `dxs` processes are running against the same workspace.
- If no other process is running, the lock file may be stale from a crash. It is safe to delete it:
  ```
  rm .dx/snaps.lock
  ```
- Retry the operation.

---

## Crash recovery

If `dxs` is interrupted mid-transaction (e.g. by a power failure or a forced kill), the next `apply` or `checkout` operation against the same session will automatically detect the orphaned `pending_transaction` record and roll the working tree back to the last known-good snapshot before proceeding.

No manual intervention is required unless the pending transaction belongs to a **different session**, in which case the operation will fail with:

```
error: Pending transaction on session <id>.
```

Resolve this by manually deleting the `pending_transaction` record from `snap.db`, or by re-initialising the workspace.

---

## Binary or unreadable files skipped during pack

**Cause:** `dxs pack` silently skips files that are binary (more than 1% null bytes or non-printable control characters in the first 8 KB) or that cannot be read as valid UTF-8.

**Resolution:**

- This is expected behavior. Binary files (executables, images, archives, databases) are intentionally excluded.
- Use `--file-type <ext>` to limit packing to specific text file types.
- The count of skipped files is reported when writing to a file via `--out`.

---

## Structured error codes

Structured error codes are planned for a future release. In v0.2.0, all errors are
reported as human-readable messages paired with the standard exit codes listed above.
