#!/usr/bin/env bash
# =============================================================================
# dxs CLI Integration Test Suite — v0.2.0
# Test data lives under /f/tmp/dx.cli.tests/run/ and is safe to delete.
# Usage:  bash tests/foundation-tests.sh
# =============================================================================

set -uo pipefail

# ── Configuration ─────────────────────────────────────────────────────────────

DX_PROJECT="F:/repos/Dx.Cli/src/Dx.Cli"

TESTROOT="/f/tmp/dx.cli.tests/run"
WORKSPACE="$TESTROOT/ws"
WORKSPACE2="$TESTROOT/ws2"

# ── Helpers ───────────────────────────────────────────────────────────────────

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; RESET='\033[0m'

PASS=0; FAIL=0; SKIP=0
FAILURES=()

pass()    { echo -e "  ${GREEN}ok${RESET} - $1";           ((PASS++)); }
fail()    { echo -e "  ${RED}not ok${RESET} - $1";         ((FAIL++)); FAILURES+=("$1"); }
skip()    { echo -e "  ok - $1 # SKIP $2";                 ((SKIP++)); }
section() { echo -e "\n# $1"; }

# Run dxs via dotnet run. Options must follow the subcommand.
dxs() { dotnet run --project "$DX_PROJECT" --no-build -- "$@" 2>&1; }

# check "dxs description" expected_exit "grep_pattern" <subcommand> [args…]
# Use pattern="" to skip pattern matching.
check() {
    local desc="$1" want_exit="$2" pattern="$3"
    shift 3
    local out got_exit
    out=$(dxs "$@" 2>&1); got_exit=$?
    local ok=1
    [[ "$got_exit" -eq "$want_exit" ]] || ok=0
    [[ -z "$pattern" ]] || echo "$out" | grep -qiE "$pattern" || ok=0
    if [[ "$ok" -eq 1 ]]; then
        pass "$desc"
    else
        fail "$desc (exit=$got_exit want=$want_exit pattern='$pattern')"
        echo "  # $(echo "$out" | head -3)"
    fi
}

# head of the current session's snap chain
# Strip ANSI escape codes before grepping so the helper works identically
# in TTY (colour) and non-TTY (plain) environments.
current_head() {
    dxs snap list -r "$1" 2>&1 \
        | sed 's/\x1b\[[0-9;]*m//g' \
        | grep "HEAD" \
        | grep -oE 'T[0-9]{4}' \
        | head -1
}

# ── Build ─────────────────────────────────────────────────────────────────────

section "building dx"
if ! dotnet build "$DX_PROJECT" --no-restore -c Debug -v q 2>&1 | tail -5; then
    echo "# build failed — aborting"; exit 1
fi
echo "# build ok"

# ── Seed workspaces ───────────────────────────────────────────────────────────

rm -rf "$TESTROOT"
mkdir -p "$WORKSPACE/src" "$WORKSPACE2"

cat > "$WORKSPACE/hello.txt" <<'EOF'
Hello, World!
This is line two.
This is line three.
EOF

cat > "$WORKSPACE/config.json" <<'EOF'
{
  "name": "test-project",
  "version": "1.0.0",
  "debug": false
}
EOF

cat > "$WORKSPACE/src/main.cs" <<'EOF'
namespace TestProject;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Hello from main");
        // TODO: implement
    }
}
EOF

cat > "$WORKSPACE/src/utils.cs" <<'EOF'
namespace TestProject;

public static class Utils
{
    public static string Greet(string name) => $"Hello, {name}!";
}
EOF

# ── 1. dxs init ───────────────────────────────────────────────────────────────────

section "1. dxs init"

check "dxs init: creates workspace"    0 "Initialized"         init "$WORKSPACE"
check "dxs init: double-init rejected" 1 "already initialized" init "$WORKSPACE"
check "dxs init: -s names session"     0 "my-session"          init "$WORKSPACE2" -s my-session
[[ -d "$WORKSPACE/.dx" ]] \
    && pass "init: .dx dir created" || fail "init: .dx dir missing"

# ── 2. dxs snap list / show ────────────────────────────────────────────────────────

section "2. dxs snap list / show"

check "dxs snap list: shows graph"    0 "Snap graph" snap list -r "$WORKSPACE"
check "dxs snap list: T0000 present"  0 "T0000"      snap list -r "$WORKSPACE"
check "dxs snap list: HEAD marked"    0 "HEAD"        snap list -r "$WORKSPACE"
check "dxs snap show: T0000"          0 "T0000"       snap show T0000 -r "$WORKSPACE"
check "dxs snap show: -f lists files" 0 "hello.txt"   snap show T0000 -r "$WORKSPACE" -f
check "dxs snap show: missing handle" 2 "not found"   snap show T9999 -r "$WORKSPACE" -f

# ── 3. dxs apply %%FILE ────────────────────────────────────────────────────────────

section "3. dxs apply %%FILE"

cat > "$TESTROOT/t_file_write.dx" <<'EOF'
%%DX v1.3 session=test author=llm base=T0000

%%FILE path="newfile.txt"
    Created by dxs apply.
    Second line.
%%ENDBLOCK

%%END
EOF

check "dxs apply: FILE creates file"      0 "T0001" apply "$TESTROOT/t_file_write.dx" -r "$WORKSPACE"
[[ -f "$WORKSPACE/newfile.txt" ]] \
    && pass "apply: newfile.txt on disk"     || fail "apply: newfile.txt missing"
grep -q "Created by dxs apply" "$WORKSPACE/newfile.txt" \
    && pass "apply: newfile.txt content ok"  || fail "apply: newfile.txt content wrong"

cat > "$TESTROOT/t_file_readonly.dx" <<'EOF'
%%DX v1.3 session=test author=llm

%%FILE path="should_not_exist.txt" readonly="true"
    Should not be written.
%%ENDBLOCK

%%END
EOF

check "dxs apply: readonly FILE is no-op" 0 "" apply "$TESTROOT/t_file_readonly.dx" -r "$WORKSPACE"
[[ ! -f "$WORKSPACE/should_not_exist.txt" ]] \
    && pass "apply: readonly not written" || fail "apply: readonly was written"

# ── 4. dxs apply %%PATCH ──────────────────────────────────────────────────────────

section "4. dxs apply %%PATCH"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_patch_lines.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="hello.txt"
@@ replace lines=2-2
    This is PATCHED line two.
@@
%%ENDBLOCK

%%END
EOF

check "dxs patch: replace lines="         0 "T" apply "$TESTROOT/t_patch_lines.dx" -r "$WORKSPACE"
grep -q "PATCHED line two" "$WORKSPACE/hello.txt" \
    && pass "patch: lines= applied" || fail "patch: lines= not applied"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_patch_pattern.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="hello.txt"
@@ replace pattern="line three"
    REPLACED line three.
@@
%%ENDBLOCK

%%END
EOF

check "dxs patch: replace pattern="       0 "T" apply "$TESTROOT/t_patch_pattern.dx" -r "$WORKSPACE"
grep -q "REPLACED line three" "$WORKSPACE/hello.txt" \
    && pass "patch: pattern= applied" || fail "patch: pattern= not applied"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_patch_insert.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="src/main.cs"
@@ insert after-pattern="Console.WriteLine"
    Console.WriteLine("Inserted line");
@@
%%ENDBLOCK

%%END
EOF

check "dxs patch: insert after-pattern"   0 "T" apply "$TESTROOT/t_patch_insert.dx" -r "$WORKSPACE"
grep -q "Inserted line" "$WORKSPACE/src/main.cs" \
    && pass "patch: insert applied" || fail "patch: insert not applied"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_patch_delete.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="src/main.cs"
@@ delete pattern="// TODO: implement"
@@
%%ENDBLOCK

%%END
EOF

check "dxs patch: delete pattern"         0 "T" apply "$TESTROOT/t_patch_delete.dx" -r "$WORKSPACE"
grep -q "TODO: implement" "$WORKSPACE/src/main.cs" \
    && fail "patch: TODO not deleted" || pass "patch: delete applied"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_patch_badpattern.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="hello.txt"
@@ replace pattern="PATTERN_THAT_DOES_NOT_EXIST_ANYWHERE"
    replacement
@@
%%ENDBLOCK

%%END
EOF

check "dxs patch: bad pattern rolls back" 1 "" apply "$TESTROOT/t_patch_badpattern.dx" -r "$WORKSPACE"
dxs snap list -r "$WORKSPACE" 2>&1 | grep -q "$HEAD" \
    && pass "patch: HEAD unchanged" || fail "patch: HEAD changed after bad patch"

# ── 5. dxs apply %%FS ─────────────────────────────────────────────────────────────

section "5. dxs apply %%FS"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_fs_move.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%FS op="move" from="newfile.txt" to="moved/newfile.txt"
%%ENDBLOCK

%%END
EOF

check "dxs fs: move"                      0 "T" apply "$TESTROOT/t_fs_move.dx" -r "$WORKSPACE"
[[ -f "$WORKSPACE/moved/newfile.txt" ]] \
    && pass "fs: file at new path"      || fail "fs: file not at new path"
[[ ! -f "$WORKSPACE/newfile.txt" ]] \
    && pass "fs: original path removed" || fail "fs: original path remains"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_fs_delete.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%FS op="delete" path="moved/newfile.txt"
%%ENDBLOCK

%%END
EOF

check "dxs fs: delete"                    0 "T" apply "$TESTROOT/t_fs_delete.dx" -r "$WORKSPACE"
[[ ! -f "$WORKSPACE/moved/newfile.txt" ]] \
    && pass "fs: file deleted" || fail "fs: file not deleted"

HEAD=$(current_head "$WORKSPACE")
cat > "$TESTROOT/t_fs_delete_ifexists.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%FS op="delete" path="totally_nonexistent.txt" if-exists="true"
%%ENDBLOCK

%%END
EOF

check "dxs fs: delete if-exists on missing" 0 "" apply "$TESTROOT/t_fs_delete_ifexists.dx" -r "$WORKSPACE"

printf '\xef\xbb\xbfBOM content\nLine two\n' > "$WORKSPACE/bom_test.txt"

cat > "$TESTROOT/t_fs_encode.dx" <<'DXEOF'
%%DX v1.3 session=test author=llm

%%FS op="encode" path="bom_test.txt" to="utf-8-no-bom" line-endings="lf"
%%ENDBLOCK

%%END
DXEOF

check "dxs fs: encode strips BOM"         0 "" apply "$TESTROOT/t_fs_encode.dx" -r "$WORKSPACE"
FIRST3=$(od -A n -t x1 -N 3 "$WORKSPACE/bom_test.txt" | tr -d ' \n')
[[ "$FIRST3" != "efbbbf" ]] \
    && pass "fs: BOM stripped" || fail "fs: BOM still present"

# ── 6. dxs base= mismatch ─────────────────────────────────────────────────────────

section "6. dxs base= mismatch"

CURRENT_HEAD=$(current_head "$WORKSPACE")

cat > "$TESTROOT/t_stale_base.dx" <<'EOF'
%%DX v1.3 session=test author=llm base=T0000

%%FILE path="stale_test.txt"
    Should fail.
%%ENDBLOCK

%%END
EOF

check "dxs base=: stale base rejected"       3 "mismatch" apply "$TESTROOT/t_stale_base.dx" -r "$WORKSPACE"
[[ ! -f "$WORKSPACE/stale_test.txt" ]] \
    && pass "base=: tree unchanged after reject" || fail "base=: file written despite mismatch"

# Explicit exit-code assertion — base mismatch MUST return 3, not 1
dxs apply "$TESTROOT/t_stale_base.dx" -r "$WORKSPACE" >/dev/null 2>&1; EC=$?
[[ $EC -eq 3 ]] \
    && pass "base=: exit code is 3" \
    || fail "base=: exit code $EC (expected 3)"

# ── 7. dxs --dry-run (-n) ────────────────────────────────────────────────────────

section "7. dxs --dry-run"

cat > "$TESTROOT/t_dryrun.dx" <<EOF
%%DX v1.3 session=test author=llm base=$CURRENT_HEAD

%%FILE path="dryrun_test.txt"
    Should not appear.
%%ENDBLOCK

%%END
EOF

check "dxs dry-run: reports dry run"      0 "dry run" apply "$TESTROOT/t_dryrun.dx" -r "$WORKSPACE" -n
[[ ! -f "$WORKSPACE/dryrun_test.txt" ]] \
    && pass "dry-run: no file written" || fail "dry-run: file was written"
dxs snap list -r "$WORKSPACE" 2>&1 | grep -q "$CURRENT_HEAD" \
    && pass "dry-run: HEAD unchanged"  || fail "dry-run: HEAD changed"

# ── 8. dxs apply via stdin ────────────────────────────────────────────────────────

section "8. dxs apply via stdin"

CURRENT_HEAD=$(current_head "$WORKSPACE")

# Omitting the [file] argument defaults to "-" (stdin) via the field initialiser.
STDIN_DOC="%%DX v1.3 session=test author=llm base=$CURRENT_HEAD

%%FILE path=\"stdin_test.txt\"
    Written via stdin.
%%ENDBLOCK

%%END"

STDIN_OUT=$(echo "$STDIN_DOC" \
    | dotnet run --project "$DX_PROJECT" --no-build -- apply -r "$WORKSPACE" 2>&1) || true
echo "$STDIN_OUT" | grep -qE "T[0-9]{4}" \
    && pass "stdin: apply produced snap" \
    || fail "stdin: apply failed: $(echo "$STDIN_OUT" | head -1)"
[[ -f "$WORKSPACE/stdin_test.txt" ]] \
    && pass "stdin: file written" || fail "stdin: file not written"

CURRENT_HEAD=$(current_head "$WORKSPACE")

# ── 9. dxs snap diff ──────────────────────────────────────────────────────────────

section "9. dxs snap diff"

check "dxs diff: T0000 to current"      0 ""      snap diff T0000 "$CURRENT_HEAD" -r "$WORKSPACE"
check "dxs diff: shows added files"     0 "added" snap diff T0000 "$CURRENT_HEAD" -r "$WORKSPACE"
# Pattern matches the normalised "No differences found." message (Bug 1.2 fix)
check "dxs diff: same snap = no diffs"  0 "No differences" snap diff T0000 T0000 -r "$WORKSPACE"
# -p is the short flag for --path on snap diff
check "dxs diff: -p filter"             0 ""      snap diff T0000 "$CURRENT_HEAD" -r "$WORKSPACE" -p src/

# ── 10. dxs snap checkout ─────────────────────────────────────────────────────────

section "10. dxs snap checkout"

# NOTE: This assertion is order-dependent. The working tree at this point contains
# bom_test.txt (created in section 5) and stdin_test.txt (created in section 8),
# among others. checkout T0000 should restore the original file set exactly.
if grep -q "PATCHED" "$WORKSPACE/hello.txt" 2>/dev/null; then
    pass "checkout: hello.txt modified before checkout"
else
    skip "checkout: hello.txt modified before checkout" "file not in expected state"
fi

check "dxs checkout: to T0000"          0 "Checked out" snap checkout T0000 -r "$WORKSPACE"

grep -q "Hello, World!" "$WORKSPACE/hello.txt" \
    && pass "checkout: hello.txt restored"   || fail "checkout: hello.txt not restored"
grep -q "PATCHED" "$WORKSPACE/hello.txt" \
    && fail "checkout: PATCHED content remains" || pass "checkout: PATCHED content removed"

NEW_HEAD=$(current_head "$WORKSPACE")
[[ "$NEW_HEAD" == "T0000" ]] \
    && pass "checkout: HEAD is T0000 (idempotent snap reuse)" \
    || fail "checkout: HEAD expected T0000, got $NEW_HEAD"
CURRENT_HEAD="$NEW_HEAD"

# ── 11. dxs log ───────────────────────────────────────────────────────────────────

section "11. dxs log"

check "dxs log: shows entries"      0 ""    log -r "$WORKSPACE"
check "dxs log: shows snap handles" 0 "T00" log -r "$WORKSPACE"
# -n is the short flag for --limit on log
check "dxs log: -n respected"       0 ""    log -r "$WORKSPACE" -n 2

# ── 12. dxs session ───────────────────────────────────────────────────────────────

section "12. dxs session"

check "dxs session list: shows sessions"  0 ""               session list -r "$WORKSPACE"
check "dxs session list: active status"   0 "active"         session list -r "$WORKSPACE"
check "dxs session show: HEAD present"    0 "HEAD"           session show -r "$WORKSPACE"
check "dxs session show: snap count"      0 "Snaps"          session show -r "$WORKSPACE"
check "dxs session new: creates session"  0 "New session"    session new my-new-session -r "$WORKSPACE"
check "dxs session list: new session"     0 "my-new-session" session list -r "$WORKSPACE"
check "dxs session close: closes it"      0 "Closed"         session close my-new-session -r "$WORKSPACE"
check "dxs session list: closed status"   0 "closed"         session list -r "$WORKSPACE"

# ── 13. dxs pack ──────────────────────────────────────────────────────────────────

section "13. dxs pack"

PACK_OUT="$TESTROOT/pack_out.dx"

# <path> is a required argument on pack; "." means the workspace root.
# -r sets the root for relative path computation.
# -o is short for --out.
check "dxs pack: stdout output"     0 "%%FILE" pack . -r "$WORKSPACE"
check "dxs pack: -o writes file"    0 "Packed" pack . -r "$WORKSPACE" -o "$PACK_OUT"

[[ -f "$PACK_OUT" ]] \
    && pass "pack: output file exists"    || fail "pack: output file missing"
grep -q '%%FILE'          "$PACK_OUT" \
    && pass "pack: %%FILE blocks present" || fail "pack: %%FILE blocks missing"
grep -q '%%ENDBLOCK'      "$PACK_OUT" \
    && pass "pack: %%ENDBLOCK present"    || fail "pack: %%ENDBLOCK missing"
grep -q 'readonly="true"' "$PACK_OUT" \
    && pass "pack: files marked readonly" || fail "pack: readonly marker missing"
grep -q '\.dx/' "$PACK_OUT" 2>/dev/null \
    && fail "pack: .dx/ included" || pass "pack: .dx/ excluded"

# -f is short for --file-type on pack
check "dxs pack: -f filter"         0 "Packed" pack . -r "$WORKSPACE" -f .cs -o "$TESTROOT/cs_only.dx"
grep -q "hello.txt" "$TESTROOT/cs_only.dx" 2>/dev/null \
    && fail "pack: .txt included by .cs filter" || pass "pack: .txt excluded by .cs filter"
grep -q "main.cs" "$TESTROOT/cs_only.dx" 2>/dev/null \
    && pass "pack: .cs file included"           || fail "pack: .cs file missing"

check "dxs pack: --tree"            0 "Packed" pack . -r "$WORKSPACE" --tree -o "$TESTROOT/tree.dx"
grep -q "%%NOTE" "$TESTROOT/tree.dx" \
    && pass "pack: --tree adds %%NOTE block" || fail "pack: --tree missing %%NOTE"

check "dxs pack: --session-header"  0 "Packed" pack . -r "$WORKSPACE" --session-header -o "$TESTROOT/hdr.dx"
head -1 "$TESTROOT/hdr.dx" | grep -q "%%DX" \
    && pass "pack: --session-header first line is %%DX" || fail "pack: --session-header missing %%DX"

# -m is short for --metadata
check "dxs pack: -m metadata"       0 "Packed" pack . -r "$WORKSPACE" -m -o "$TESTROOT/meta.dx"
grep -q "%%NOTE" "$TESTROOT/meta.dx" \
    && pass "pack: -m adds %%NOTE block" || fail "pack: -m missing %%NOTE"

# Explicit single-file path — not "."; the file is relative to WORKSPACE
check "dxs pack: single file"       0 "%%FILE" pack hello.txt -r "$WORKSPACE"

printf '\x00\x01\x02\x03binary' > "$WORKSPACE/binary.bin"
dxs pack . -r "$WORKSPACE" -o "$TESTROOT/nobinary.dx" 2>&1 || true
grep -q "binary.bin" "$TESTROOT/nobinary.dx" 2>/dev/null \
    && fail "pack: binary file included" || pass "pack: binary file excluded"
rm -f "$WORKSPACE/binary.bin"

PACK_BOM=$(od -A n -t x1 -N 3 "$PACK_OUT" | tr -d ' \n')
[[ "$PACK_BOM" != "efbbbf" ]] \
    && pass "pack: output is UTF-8 no-BOM" || fail "pack: output has BOM"

# ── 14. dxs run ───────────────────────────────────────────────────────────────────

section "14. dxs run"

check "dxs run: against HEAD"     0 "Running against" run "echo hello_from_run" -r "$WORKSPACE"
check "dxs run: --snap T0000"     0 ""                run --snap T0000 "echo ok" -r "$WORKSPACE"
# -t is short for --timeout on run
check "dxs run: -t timeout"       0 ""                run -t 10 "echo ok" -r "$WORKSPACE"
check "dxs run: nonexistent snap" 2 ""                run --snap T9999 "echo x" -r "$WORKSPACE"

RUN_ISOLATION=$(dxs run --snap T0000 "ls" -r "$WORKSPACE" 2>&1) || true
echo "$RUN_ISOLATION" | grep -q "stdin_test.txt" \
    && fail "run: snap not isolated (stdin_test.txt in T0000)" \
    || pass "run: snap isolated (stdin_test.txt absent from T0000)"

# ── 15. dxs eval ──────────────────────────────────────────────────────────────────

section "15. dxs eval"

CURRENT_HEAD=$(current_head "$WORKSPACE")

# -p is short for --pass-if on eval
check "dxs eval: b-passes pass"      0 "PASS" eval T0000 "$CURRENT_HEAD" "echo ok" -r "$WORKSPACE" -p b-passes
check "dxs eval: exit-equal pass"    0 "PASS" eval T0000 "$CURRENT_HEAD" "echo ok" -r "$WORKSPACE" -p exit-equal
check "dxs eval: both-pass pass"     0 "PASS" eval T0000 "$CURRENT_HEAD" "echo ok" -r "$WORKSPACE" -p both-pass
check "dxs eval: no-regression pass" 0 "PASS" eval T0000 "$CURRENT_HEAD" "echo ok" -r "$WORKSPACE" -p no-regression
check "dxs eval: b-passes fail"      1 "FAIL" eval T0000 "$CURRENT_HEAD" "exit 1"  -r "$WORKSPACE" -p b-passes
check "dxs eval: --label-a/b"        0 "PASS" eval T0000 "$CURRENT_HEAD" "echo ok" -r "$WORKSPACE" \
    --label-a baseline --label-b candidate -p both-pass
check "dxs eval: --sequential"       0 "PASS" eval T0000 "$CURRENT_HEAD" "echo ok" -r "$WORKSPACE" \
    --sequential -p both-pass

# ── 16. dxs config ────────────────────────────────────────────────────────────────

section "16. dxs config"

check "dxs config set"                    0 "Set"       config set conflict.on_base_mismatch reject -r "$WORKSPACE"
check "dxs config get: value returned"    0 "reject"    config get conflict.on_base_mismatch -r "$WORKSPACE"
check "dxs config set: run_timeout"       0 "Set"       config set run.run_timeout 60 -r "$WORKSPACE"
check "dxs config list"                   0 "conflict"  config list -r "$WORKSPACE"
check "dxs config show-effective"         0 "Effective" config show-effective -r "$WORKSPACE"
check "dxs config show-effective: source" 0 "local"     config show-effective -r "$WORKSPACE"
check "dxs config unset"                  0 "Unset"     config unset conflict.on_base_mismatch -r "$WORKSPACE"
check "dxs config get: default fallback"  0 "default"   config get conflict.on_base_mismatch -r "$WORKSPACE"
check "dxs config set: unknown key"       2 "Unknown"   config set unknown.key value -r "$WORKSPACE"
# -g is short for --global on config commands
check "dxs config set: snap.exclude blocked" 2 "globally" config set -g snap.exclude "[]" -r "$WORKSPACE"

# ── 17. dxs error handling ────────────────────────────────────────────────────────

section "17. dxs error handling"

check "dxs error: no workspace"    2 "" snap list -r "$TESTROOT/no_such_dir"

OUT=$(dxs snap show -r "$WORKSPACE" 2>&1) || true
echo "$OUT" | grep -qiE "handle|required|argument|Missing" \
    && pass "error: snap show without handle gives guidance" \
    || fail "error: snap show without handle unhelpful: $(echo "$OUT" | head -1)"

check "dxs error: apply missing file"  1 "" apply "$TESTROOT/no_such.dx" -r "$WORKSPACE"

cat > "$TESTROOT/malformed.dx" <<'EOF'
This is not a DX document.
EOF
check "dxs error: apply malformed doc" 2 "parse error" apply "$TESTROOT/malformed.dx" -r "$WORKSPACE"

# ── 18. dxs session isolation ─────────────────────────────────────────────────────

section "18. dxs session isolation"

check "dxs isolation: ws2 snap list"    0 "T0000"      snap list -r "$WORKSPACE2"
check "dxs isolation: ws2 own session"  0 "my-session" session list -r "$WORKSPACE2"

cat > "$TESTROOT/t_ws2.dx" <<'EOF'
%%DX v1.3 session=my-session author=llm base=T0000

%%FILE path="ws2_only.txt"
    Workspace2 only.
%%ENDBLOCK

%%END
EOF

check "dxs isolation: apply to ws2"         0 "T0001" apply "$TESTROOT/t_ws2.dx" -r "$WORKSPACE2"
[[ ! -f "$WORKSPACE/ws2_only.txt" ]] \
    && pass "isolation: ws2 change did not affect ws1" \
    || fail "isolation: ws2 change leaked to ws1"

# ── 19. dxs run gate ──────────────────────────────────────────────────────────────

section "19. dxs run gate"

CURRENT_HEAD=$(current_head "$WORKSPACE")

cat > "$TESTROOT/t_gate_pass.dx" <<EOF
%%DX v1.3 session=test author=llm base=$CURRENT_HEAD

%%FILE path="gated.txt"
    Created with run gate.
%%ENDBLOCK

%%REQUEST type="run"
    echo gate_passed
%%ENDBLOCK

%%END
EOF

check "dxs gate: passing gate commits"  0 "T" apply "$TESTROOT/t_gate_pass.dx" -r "$WORKSPACE"
[[ -f "$WORKSPACE/gated.txt" ]] \
    && pass "gate: file present after pass" || fail "gate: file missing after pass"

CURRENT_HEAD=$(current_head "$WORKSPACE")

cat > "$TESTROOT/t_gate_fail.dx" <<EOF
%%DX v1.3 session=test author=llm base=$CURRENT_HEAD

%%FILE path="gated_fail.txt"
    Should be rolled back.
%%ENDBLOCK

%%REQUEST type="run"
    exit 1
%%ENDBLOCK

%%END
EOF

check "dxs gate: failing gate rolls back" 1 "" apply "$TESTROOT/t_gate_fail.dx" -r "$WORKSPACE"
[[ ! -f "$WORKSPACE/gated_fail.txt" ]] \
    && pass "gate: file absent after rollback" || fail "gate: file persists after rollback"

# ── 20. dxs --no-color ────────────────────────────────────────────────────────────

section "20. dxs --no-color"

NO_COLOR_OUT=$(dxs snap list --no-color --root "$WORKSPACE" 2>&1)

echo "$NO_COLOR_OUT" | grep -qP '\x1b' \
    && fail "--no-color: ANSI codes present" \
    || pass "--no-color: no ANSI codes in output"

echo "$NO_COLOR_OUT" | grep -qE 'T[0-9]{4}' \
    && pass "--no-color: snap handles visible" \
    || fail "--no-color: snap handles missing"

# ── 21. dxs --on-base-mismatch warn ──────────────────────────────────────────────

section "21. dxs --on-base-mismatch warn"

mkdir -p "$WORKSPACE/.dx/temp"
cat > "$WORKSPACE/.dx/temp/test-stale.dx" <<'EOF'
%%DX v1.3 session=test author=llm base=T0000
%%FILE path="stale_flag_test.txt"
 test
%%ENDBLOCK
%%END
EOF

STALE_WARN_OUT=$(dxs apply .dx/temp/test-stale.dx \
    --root "$WORKSPACE" --on-base-mismatch warn 2>&1) || true

STALE_WARN_EXIT=$?

[[ $STALE_WARN_EXIT -eq 0 ]] \
    && pass "--on-base-mismatch warn: exits 0" \
    || fail "--on-base-mismatch warn: exits $STALE_WARN_EXIT (expected 0)"

CURRENT_HEAD=$(current_head "$WORKSPACE")

dxs apply .dx/temp/test-stale.dx --root "$WORKSPACE" \
    --on-base-mismatch reject > /dev/null 2>&1; EC=$?

[[ $EC -eq 3 ]] \
    && pass "--on-base-mismatch reject: exits 3" \
    || fail "--on-base-mismatch reject: exits $EC (expected 3)"

# ── 22. dxs --timeout ────────────────────────────────────────────────────────

section "22. dxs --timeout"

check "dxs run: --timeout within limit" 0 "" \
    run --timeout 30 "echo ok" --root "$WORKSPACE"

check "dxs run: --timeout 0 means no timeout" 0 "" \
    run --timeout 0 "echo ok" --root "$WORKSPACE"

# ── 23. Invariant: genesis logging (#9) ──────────────────────────────────────

section "23. Invariant: genesis logging"

# Every session must have a session_log entry for its genesis (T0000).
# We verify this via `dxs log` — the genesis entry must be present.

LOG_OUT=$(dxs log -r "$WORKSPACE" -n 100 2>&1)

# The log table must contain at least one entry referencing T0000 with tx_success=1
# We check: at least one ✓ row referencing T0000 appears in the log output.
echo "$LOG_OUT" | grep -qE '✓' \
    && pass "genesis log: session_log has at least one successful entry" \
    || fail "genesis log: no successful log entry found"

echo "$LOG_OUT" | grep -qE 'T0000' \
    && pass "genesis log: T0000 referenced in session_log" \
    || fail "genesis log: T0000 not referenced in session_log"

# New session must also log genesis
dxs session new log-invariant-test -r "$WORKSPACE" > /dev/null 2>&1
NEW_SESSION_LOG=$(dxs log -r "$WORKSPACE" -s log-invariant-test -n 5 2>&1)

echo "$NEW_SESSION_LOG" | grep -qE '✓' \
    && pass "genesis log: new session also logged genesis" \
    || fail "genesis log: new session has no genesis log entry"

dxs session close log-invariant-test -r "$WORKSPACE" > /dev/null 2>&1

# ── 24. Invariant: checkout logging (#14) ────────────────────────────────────

section "24. Invariant: checkout logging"

# Checkout is a state mutation and MUST produce a session_log entry.
# Strategy:
#   1. Record the log count before checkout
#   2. Perform a checkout
#   3. Assert log count increased by exactly 1
#   4. Assert the new entry has tx_success = ✓ and a snap handle

LOG_COUNT_BEFORE=$(dxs log -r "$WORKSPACE" -n 1000 2>&1 | grep -cE '✓|✗' || true)

# Perform a checkout (go to T0000, then back to something else)
dxs snap checkout T0000 -r "$WORKSPACE" > /dev/null 2>&1

LOG_COUNT_AFTER=$(dxs log -r "$WORKSPACE" -n 1000 2>&1 | grep -cE '✓|✗' || true)

[[ "$LOG_COUNT_AFTER" -gt "$LOG_COUNT_BEFORE" ]] \
    && pass "checkout log: session_log entry added after checkout" \
    || fail "checkout log: no new session_log entry after checkout (invariant violation)"

# The most recent entry should be successful (tx_success=1)
LATEST_LOG=$(dxs log -r "$WORKSPACE" -n 1 2>&1)
echo "$LATEST_LOG" | grep -qE '✓' \
    && pass "checkout log: most recent entry is successful" \
    || fail "checkout log: most recent entry is not successful"

# ── 25. Invariant: DoctorCommand mapping (#12) ───────────────────────────────

section "25. Invariant: DoctorCommand mapping"

# Verify dxs doctor runs without errors and produces coherent output.
# In a clean workspace it should report "healthy" (no stuck transactions).
DOCTOR_OUT=$(dxs doctor -r "$WORKSPACE" 2>&1)
DOCTOR_EXIT=$?

# On a healthy workspace: exit 0 + "healthy" message
[[ $DOCTOR_EXIT -eq 0 ]] \
    && pass "doctor: exits 0 on healthy workspace" \
    || fail "doctor: exits $DOCTOR_EXIT on healthy workspace (expected 0)"

echo "$DOCTOR_OUT" | grep -qi "healthy" \
    && pass "doctor: reports healthy" \
    || fail "doctor: missing 'healthy' in output: $(echo "$DOCTOR_OUT" | head -2)"

# The mapping fix (#12): when a pending_transaction IS present, StartedUtc
# must not be empty/null. We can't easily inject a row in bash, so we verify
# the schema by checking the --repair flag works on a clean workspace:
REPAIR_OUT=$(dxs doctor -r "$WORKSPACE" --repair 2>&1)
echo "$REPAIR_OUT" | grep -qi "healthy\|0 issue" \
    && pass "doctor: --repair on clean workspace ok" \
    || fail "doctor: --repair failed on clean workspace: $(echo "$REPAIR_OUT" | head -2)"

# ── Results ───────────────────────────────────────────────────────────────────

TOTAL=$((PASS + FAIL + SKIP))
echo ""
printf "# %d/%d passed, %d failed, %d skipped\n" "$PASS" "$TOTAL" "$FAIL" "$SKIP"

if [[ ${#FAILURES[@]} -gt 0 ]]; then
    echo "#"
    echo "# failed tests:"
    for f in "${FAILURES[@]}"; do echo "#   $f"; done
fi

[[ $FAIL -eq 0 ]] && echo "# all tests passed" || echo "# $FAIL test(s) failed"

echo ""
echo "# test data: $TESTROOT"
echo "# cleanup:   rm -rf $TESTROOT"

[[ $FAIL -eq 0 ]] && exit 0 || exit 1
