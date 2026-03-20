# dx

`dx` is a transactional command-line interface for deterministic workspace mutation, snapshotting, and differential execution.

It enables reproducible changes, verifiable execution, and explicit state transitions within a local workspace.

---

## Installation

### Windows (Recommended)

```powershell
winget install ulfbou.dx
````

### Manual / Cross-Platform

Download a release for your platform from the GitHub Releases page and place the binary on your `PATH`.

---

## Quick Start

Initialize a workspace:

```
dxs init
```

Apply a dxs document:

```
dxs apply changes.dx
```

Inspect snapshots:

```
dxs snap list
dxs snap show T0001 --files
```

Run a command:

```
dxs run "dotnet test"
```

Compare behavior across snapshots:

```
dxs eval T0001 T0005 "dotnet test"
```

---

## Concepts

### Workspace

A directory containing a `.dx/` folder and associated state.

### Transaction

A validated and atomic application of a dxs document.

### Snapshot

An immutable representation of workspace state after a successful transaction.

### Session

A logical sequence of transactions and snapshots.

---

## Documentation

* `docs/CLI.md` — Command reference
* `docs/CONFIGURATION.md` — Configuration system
* `docs/TROUBLESHOOTING.md` — Errors and recovery

---

## Design Principles

* Deterministic execution
* Explicit state transitions
* Atomic mutations
* Reproducible environments

