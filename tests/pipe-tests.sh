#!/usr/bin/env bash
# =============================================================================
# dxs pipe validation — tests the pack | processor | apply pipeline
#
# Usage:  bash tests/pipe-test.sh [--dxs-bin <path>]
#
# By default uses 'dxs' from PATH for the apply end of the pipe.
# The pack end uses dotnet run (dev build) so we test our changes.
#
# Override the binary: bash tests/pipe-test.sh --dxs-bin ./publish/dxs.exe
#
# Why two different invocations?
#   dotnet run reads stdin itself before the app starts, consuming the pipe.
#   The installed binary (dxs) receives stdin correctly.
#   In real use: dxs pack ... | llm | dxs apply -
#   Both ends use the same binary. We simulate that here.
# =============================================================================

set -uo pipefail

# ── Arguments ─────────────────────────────────────────────────────────────────

DX_PROJECT="${DX_PROJECT:-src/Dx.Cli}"

# Use a Windows-native path for the test workspace so that the installed
# native binary and the Git Bash environment both resolve it identically.
# cygpath -w converts /tmp/... to C:\Users\...\Temp\...
if command -v cygpath >/dev/null 2>&1; then
    TESTROOT="${TESTROOT:-$(cygpath -w /tmp/dxs-pipe-test)}"
else
    TESTROOT="${TESTROOT:-/tmp/dxs-pipe-test}"
fi
WORKSPACE="$TESTROOT/ws"
WORKSPACE_WIN="$WORKSPACE"

# The binary used for the APPLY end of the pipe.
# Must be an installed binary (not dotnet run) to receive stdin correctly.
DXS_BIN="${DXS_BIN:-dxs}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dxs-bin) DXS_BIN="$2"; shift 2 ;;
        *) shift ;;
    esac
done

if ! command -v "$DXS_BIN" >/dev/null 2>&1 && [[ ! -f "$DXS_BIN" ]]; then
    echo "error: apply binary not found: $DXS_BIN"
    echo "  Install dxs first:  dotnet tool install -g dxs"
    echo "  Or specify:         --dxs-bin ./publish/dxs"
    exit 2
fi

# ── Helpers ───────────────────────────────────────────────────────────────────

RED='\033[0;31m'; GREEN='\033[0;32m'; RESET='\033[0m'
PASS=0; FAIL=0
FAILURES=()

pass() { echo -e "  ${GREEN}ok${RESET} - $1"; ((PASS++)) || true; }
fail() { echo -e "  ${RED}not ok${RESET} - $1"; ((FAIL++)) || true; FAILURES+=("$1"); }

# Dev build — used ONLY for pack (needs dotnet run for stdout pipe).
pack() { dotnet run --project "$DX_PROJECT" --no-build -- pack "$@" 2>/dev/null; }

# Installed binary — used for ALL workspace operations.
# All paths passed to it must be Windows-native (WORKSPACE_WIN).
apply_stdin() { "$DXS_BIN" apply --root "$WORKSPACE_WIN" 2>&1; }

head_of() {
    "$DXS_BIN" snap list --root "$WORKSPACE_WIN" 2>/dev/null \
        | sed 's/\x1b\[[0-9;]*m//g' \
        | grep -oE 'T[0-9]{4}' \
        | tail -1
}

echo "# dxs pipe validation"
echo "# pack:  dotnet run --project $DX_PROJECT"
echo "# apply: $DXS_BIN"
echo ""

# ── Setup ─────────────────────────────────────────────────────────────────────

rm -rf "$TESTROOT"
mkdir -p "$WORKSPACE/src"

cat > "$WORKSPACE/hello.txt" <<'EOF'
Hello from the pipe test.
Line two.
EOF

cat > "$WORKSPACE/src/main.cs" <<'EOF'
namespace PipeTest;
public class App
{
    public static void Main() => System.Console.WriteLine("pipe test");
}
EOF

"$DXS_BIN" init "$WORKSPACE_WIN" --session pipe-test
SESSION="pipe-test"
echo ""

# ── Test 1: pack stdout cleanliness ───────────────────────────────────────────

echo "# 1. pack stdout cleanliness"

PACK_DOC=$(pack . -r "$WORKSPACE_WIN" --session-header)
PACK_EXIT=$?

[[ $PACK_EXIT -eq 0 ]] \
    && pass "pack: exits 0" \
    || fail "pack: exits $PACK_EXIT (expected 0)"

echo "$PACK_DOC" | grep -qP '\x1b' \
    && fail "pack stdout: contains ANSI escape codes" \
    || pass "pack stdout: no ANSI escape codes"

echo "$PACK_DOC" | head -1 | grep -q "^%%DX" \
    && pass "pack stdout: begins with %%DX header" \
    || fail "pack stdout: missing %%DX header"

echo "$PACK_DOC" | grep -q "^%%END" \
    && pass "pack stdout: contains %%END" \
    || fail "pack stdout: missing %%END"

NONREADONLY=$(echo "$PACK_DOC" | grep "^%%FILE" | grep -v 'readonly="true"' | wc -l)
[[ "$NONREADONLY" -eq 0 ]] \
    && pass "pack stdout: all %%FILE blocks are readonly" \
    || fail "pack stdout: $NONREADONLY %%FILE block(s) missing readonly=true"

echo "$PACK_DOC" | grep -qi "skipped" \
    && fail "pack stdout: contains diagnostic noise" \
    || pass "pack stdout: no diagnostic noise"

echo ""

# ── Test 2: identity pipe — read-only doc is a no-op ─────────────────────────

echo "# 2. identity pipe: pack | cat | apply -"

HEAD_BEFORE=$(head_of "$WORKSPACE")

# A read-only document applied should exit 0 and not advance HEAD.
APPLY_OUT=$(pack . -r "$WORKSPACE_WIN" --session-header \
    | cat \
    | apply_stdin)
APPLY_EXIT=$?

[[ $APPLY_EXIT -eq 0 ]] \
    && pass "identity pipe: exits 0" \
    || { fail "identity pipe: exits $APPLY_EXIT"; echo "  # $(echo "$APPLY_OUT" | head -2)"; }

HEAD_AFTER=$(head_of "$WORKSPACE")
[[ "$HEAD_BEFORE" == "$HEAD_AFTER" ]] \
    && pass "identity pipe: HEAD unchanged (read-only is a no-op)" \
    || fail "identity pipe: HEAD changed unexpectedly ($HEAD_BEFORE → $HEAD_AFTER)"

echo ""

# ── Test 3: mutating pipe — create a new file ─────────────────────────────────

echo "# 3. mutating pipe: pack | processor | apply -"

HEAD_BEFORE=$(head_of "$WORKSPACE")

# The processor strips the read-only pack's %%END, then appends a proper
# mutating DX document (with session= and base=) followed by %%END.
# This is the canonical LLM output pattern: read context, emit mutations.
# The processor replaces the read-only pack header with a mutating one.
# sed strips the original %%DX header line and %%END, then the processor
# emits a complete fresh document: new header + readonly context blocks + mutation + %%END.
APPLY_OUT=$(pack . -r "$WORKSPACE_WIN" --session-header \
    | sed -e '/^%%DX /d' -e '/^%%END$/d' \
    | {
        printf '%%%%DX v1.3 session=%s author=llm base=%s\n\n' "$SESSION" "$HEAD_BEFORE"
        cat
        printf '\n%%%%FILE path="pipe_created.txt"\n    Created by the pipe test processor.\n%%%%ENDBLOCK\n\n%%%%END\n'
    } \
    | apply_stdin)
APPLY_EXIT=$?

[[ $APPLY_EXIT -eq 0 ]] \
    && pass "mutating pipe: exits 0" \
    || { fail "mutating pipe: exits $APPLY_EXIT"; echo "  # $(echo "$APPLY_OUT" | head -3)"; }

HEAD_AFTER=$(head_of "$WORKSPACE")
[[ "$HEAD_BEFORE" != "$HEAD_AFTER" ]] \
    && pass "mutating pipe: HEAD advanced ($HEAD_BEFORE → $HEAD_AFTER)" \
    || fail "mutating pipe: HEAD did not advance"

[[ -f "$WORKSPACE/pipe_created.txt" ]] \
    && pass "mutating pipe: file exists on disk" \
    || fail "mutating pipe: file missing"

if [[ -f "$WORKSPACE/pipe_created.txt" ]]; then
    grep -q "Created by the pipe test processor" "$WORKSPACE/pipe_created.txt" \
        && pass "mutating pipe: file content correct" \
        || fail "mutating pipe: file content wrong"
else
    fail "mutating pipe: file content wrong (file absent)"
fi

echo ""

# ── Test 4: patch via pipe ────────────────────────────────────────────────────

echo "# 4. patch pipe: pack | processor | apply -"

HEAD_BEFORE=$(head_of "$WORKSPACE")

APPLY_OUT=$(pack . -r "$WORKSPACE_WIN" --session-header \
    | sed -e '/^%%DX /d' -e '/^%%END$/d' \
    | {
        printf '%%%%DX v1.3 session=%s author=llm base=%s\n\n' "$SESSION" "$HEAD_BEFORE"
        cat
        printf '\n%%%%PATCH path="hello.txt"\n@@ replace pattern="Line two."\n    Line two — patched via pipe.\n@@\n%%%%ENDBLOCK\n\n%%%%END\n'
    } \
    | apply_stdin)
APPLY_EXIT=$?

[[ $APPLY_EXIT -eq 0 ]] \
    && pass "patch pipe: exits 0" \
    || { fail "patch pipe: exits $APPLY_EXIT"; echo "  # $(echo "$APPLY_OUT" | head -3)"; }

HEAD_AFTER=$(head_of "$WORKSPACE")
[[ "$HEAD_BEFORE" != "$HEAD_AFTER" ]] \
    && pass "patch pipe: HEAD advanced ($HEAD_BEFORE → $HEAD_AFTER)" \
    || fail "patch pipe: HEAD did not advance"

grep -q "patched via pipe" "$WORKSPACE/hello.txt" \
    && pass "patch pipe: patch applied to hello.txt" \
    || fail "patch pipe: patch not applied"

echo ""

# ── Test 5: bad patch rolls back ─────────────────────────────────────────────

echo "# 5. rollback pipe: bad patch → exit 1, HEAD unchanged"

HEAD_BEFORE=$(head_of "$WORKSPACE")

APPLY_OUT=$(pack . -r "$WORKSPACE_WIN" --session-header \
    | sed -e '/^%%DX /d' -e '/^%%END$/d' \
    | {
        printf '%%%%DX v1.3 session=%s author=llm base=%s\n\n' "$SESSION" "$HEAD_BEFORE"
        cat
        printf '\n%%%%PATCH path="hello.txt"\n@@ replace pattern="THIS_DOES_NOT_EXIST"\n    replacement\n@@\n%%%%ENDBLOCK\n\n%%%%END\n'
    } \
    | apply_stdin)
APPLY_EXIT=$?

[[ $APPLY_EXIT -eq 1 ]] \
    && pass "rollback pipe: exits 1 on bad patch" \
    || fail "rollback pipe: exits $APPLY_EXIT (expected 1)"

HEAD_AFTER=$(head_of "$WORKSPACE")
[[ "$HEAD_BEFORE" == "$HEAD_AFTER" ]] \
    && pass "rollback pipe: HEAD unchanged" \
    || fail "rollback pipe: HEAD changed despite failed patch"

echo ""

# ── Test 6: stderr isolation ──────────────────────────────────────────────────

echo "# 6. stderr isolation: pack stdout is clean document only"

STDOUT_FILE="$TESTROOT/stdout.dx"
STDERR_FILE="$TESTROOT/stderr.txt"

dotnet run --project "$DX_PROJECT" --no-build -- \
    pack . -r "$WORKSPACE_WIN" --session-header \
    > "$STDOUT_FILE" \
    2> "$STDERR_FILE"

head -1 "$STDOUT_FILE" | grep -q "^%%DX" \
    && pass "stderr isolation: stdout is a valid DX document" \
    || fail "stderr isolation: stdout does not begin with %%DX"

grep -qi "build succeeded\|packed\|skipped\|warning\b" "$STDOUT_FILE" \
    && fail "stderr isolation: stdout contains diagnostic text" \
    || pass "stderr isolation: stdout contains no diagnostic text"

echo ""

# ── Results ───────────────────────────────────────────────────────────────────

TOTAL=$((PASS + FAIL))
printf "# %d/%d passed, %d failed\n" "$PASS" "$TOTAL" "$FAIL"

if [[ ${#FAILURES[@]} -gt 0 ]]; then
    echo "#"
    echo "# failed tests:"
    for f in "${FAILURES[@]}"; do echo "#   $f"; done
fi

[[ $FAIL -eq 0 ]] && echo "# all pipe tests passed" || echo "# $FAIL pipe test(s) failed"

echo ""
echo "# test data: $TESTROOT"
echo "# cleanup:   rm -rf $TESTROOT"

[[ $FAIL -eq 0 ]] && exit 0 || exit 1
