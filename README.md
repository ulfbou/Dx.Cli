# dxs

`dxs` is a transactional command-line interface for deterministic workspace mutation,
snapshotting, and differential execution.

It enables reproducible changes, verifiable execution, and explicit state transitions
within a local workspace — for any language, any toolchain, any OS.

---

## Installation

### Windows

```powershell
winget install ulfbou.dxs
```

### macOS (Apple Silicon and Intel)

```bash
brew install ulfbou/tap/dxs
```

> **Note:** The Homebrew formula currently targets Apple Silicon (`osx-arm64`).
> Intel Mac (`osx-x64`) users can install via the script method below until
> multi-arch formula support is added in a future release.

### Linux and macOS (script)

```bash
curl -sSL https://raw.githubusercontent.com/ulfbou/dx.cli/main/install.sh | bash
```

The script detects your OS and architecture, downloads the correct release binary
from GitHub, and installs it to `/usr/local/bin`. Supports `linux-x64`, `linux-arm64`,
`osx-x64`, and `osx-arm64`.

> **macOS Gatekeeper:** If macOS blocks the binary with "Developer cannot be verified",
> run the following after installation:
> ```bash
> xattr -d com.apple.quarantine /usr/local/bin/dxs
> ```
> This is a one-time step. Alternatively, install via Homebrew which handles this
> automatically.

### Any platform with .NET 10 installed

```bash
dotnet tool install -g dxs
```

This is the recommended path for .NET developers. The tool is published to NuGet and
works identically on Windows, Linux, and macOS without requiring a separate download.

---

## Quick Start

Initialize a workspace:

```
dxs init
```

Apply a DX document:

```
dxs apply changes.dx
```

Inspect snapshots:

```
dxs snap list
dxs snap show T0001 --files
```

Run a command against a specific snapshot:

```
dxs run --snap T0001 "dotnet test"
```

Compare behaviour across two snapshots:

```
dxs eval T0001 T0005 "dotnet test"
```

---

## Concepts

### Workspace

A directory containing a `.dx/` folder and associated state database (`snap.db`).
Created with `dxs init`.

### Transaction

A validated and atomic application of a DX document to the workspace. Either all
mutations apply and a snapshot is produced, or nothing changes.

### Snapshot

An immutable, content-addressed record of the complete working tree state at a point
in time. Referenced by sequential handles (`T0000`, `T0001`, ...) within a session.

### Session

A named sequence of transactions and snapshots rooted at a single workspace directory.
Multiple sessions can coexist in the same workspace.

---

## Distribution Summary

| Platform | Command |
|---|---|
| Windows | `winget install ulfbou.dxs` |
| macOS | `brew install ulfbou/tap/dxs` |
| Linux / macOS | `curl -sSL https://raw.githubusercontent.com/ulfbou/dx.cli/main/install.sh \| bash` |
| Any (.NET 10) | `dotnet tool install -g dxs` |

---

## Documentation

- `docs/CLI.md` — Complete command reference
- `docs/CONFIGURATION.md` — Configuration system and precedence rules
- `docs/TROUBLESHOOTING.md` — Error codes, failure scenarios, crash recovery
- `docs/CONTRIBUTING.md` — Contributing guide and code style rules

---

## Design Principles

- Deterministic execution — same inputs always produce the same state transitions
- Explicit state transitions — every mutation is recorded and auditable
- Atomic mutations — all changes in a document apply or none do
- Reproducible environments — snapshot hashes are stable across machines and CI runs
- Language agnostic — works on any directory, any toolchain, no git required
