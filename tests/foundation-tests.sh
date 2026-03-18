#!/usr/bin/env bash
# =============================================================================
# dx CLI Integration Test Suite
# Place this script anywhere — test data goes to /f/tmp/dx.cli.tests/run/
# Usage: bash run_tests.sh
# =============================================================================

set -uo pipefail

# ── Configuration ─────────────────────────────────────────────────────────────

DX_PROJECT="F:/repos/Dx.Cli/src/Dx.Cli"

# Test data is SEPARATE from the script directory so rm -rf never touches this file
TESTROOT="/f/tmp/dx.cli.tests/run"
WORKSPACE="$TESTROOT/workspace"
WORKSPACE2="$TESTROOT/workspace2"

# ── Colour helpers ─────────────────────────────────────────────────────────────

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; RESET='\033[0m'

PASS=0; FAIL=0; SKIP=0
FAILURES=()

pass() { echo -e "  ${GREEN}✓${RESET} $1"; ((PASS++)); }
fail() { echo -e "  ${RED}✗${RESET} $1"; ((FAIL++)); FAILURES+=("$1"); }
skip() { echo -e "  ${YELLOW}~${RESET} $1 [skipped]"; ((SKIP++)); }
section() { echo -e "\n${BOLD}${CYAN}══ $1 ══${RESET}"; }

# ── dx wrapper ─────────────────────────────────────────────────────────────────
# Spectre requires options AFTER the subcommand, so we pass args as-is.
# All check() calls are written as: check ... subcmd [--root path] [args]
dx() {
    dotnet run --project "$DX_PROJECT" --no-build -- "$@" 2>&1
}

# check "desc" expected_exit "grep_pattern" subcmd [--root path] [args...]
check() {
    local desc="$1" expected_exit="$2" pattern="$3"
    shift 3
    local out actual_exit
    out=$(dx "$@" 2>&1)
    actual_exit=$?
    local ok=1
    [[ "$actual_exit" -eq "$expected_exit" ]] || ok=0
    if [[ -n "$pattern" ]]; then
        echo "$out" | grep -qiE "$pattern" || ok=0
    fi
    if [[ "$ok" -eq 1 ]]; then
        pass "$desc"
    else
        fail "$desc (exit=$actual_exit expected=$expected_exit pattern='$pattern')"
        echo "    $(echo "$out" | head -3)"
    fi
}

# ── Setup ──────────────────────────────────────────────────────────────────────

section "Setup"

echo -e "${BOLD}Building dx...${RESET}"
if dotnet build "$DX_PROJECT" --no-restore -c Debug -v q 2>&1 | tail -3; then
    pass "dotnet build succeeded"
else
    echo -e "${RED}Build failed — aborting${RESET}"; exit 1
fi

rm -rf "$TESTROOT"
mkdir -p "$WORKSPACE/src" "$WORKSPACE2" "$TESTROOT/docs"

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

pass "Test workspace seeded at $WORKSPACE"

# ── 1. dx init ────────────────────────────────────────────────────────────────

section "1. dx init"

check "init creates workspace"        0 "Initialized"         init "$WORKSPACE"
check "init double-init fails"        1 "already initialized" init "$WORKSPACE"
check "init named session"            0 "my-session"          init "$WORKSPACE2" --session my-session
check "genesis snap T0000 created"    0 "T0000"               snap list --root "$WORKSPACE"
[[ -d "$WORKSPACE/.dx" ]] && pass ".dx directory exists" || fail ".dx directory missing"

# ── 2. dx snap list / show ────────────────────────────────────────────────────

section "2. dx snap list / show"

check "snap list shows graph"         0 "Snap graph"  snap list --root "$WORKSPACE"
check "snap list shows T0000"         0 "T0000"       snap list --root "$WORKSPACE"
check "snap list marks HEAD"          0 "HEAD"        snap list --root "$WORKSPACE"
check "snap show T0000"               0 "T0000"       snap show T0000 --root "$WORKSPACE"
check "snap show --files"             0 "hello.txt"   snap show T0000 --root "$WORKSPACE" --files
check "snap show missing handle"      2 "not found"   snap show T9999 --root "$WORKSPACE" --files

# ── 3. dx apply — %%FILE ─────────────────────────────────────────────────────

section "3. dx apply — %%FILE"

cat > "$TESTROOT/t_file_write.dx" <<'EOF'
%%DX v1.3 session=test author=llm base=T0000

%%FILE path="newfile.txt"
    Created by dx apply.
    Second line.
%%ENDBLOCK

%%END
EOF

check "apply FILE creates file"       0 "T0001"  apply "$TESTROOT/t_file_write.dx" --root "$WORKSPACE"
[[ -f "$WORKSPACE/newfile.txt" ]]                    && pass "newfile.txt on disk"        || fail "newfile.txt missing"
grep -q "Created by dx apply" "$WORKSPACE/newfile.txt" && pass "newfile.txt content ok"  || fail "newfile.txt content wrong"

cat > "$TESTROOT/t_file_readonly.dx" <<'EOF'
%%DX v1.3 session=test author=llm

%%FILE path="should_not_exist.txt" readonly="true"
    Should not be written.
%%ENDBLOCK

%%END
EOF

check "apply readonly FILE is no-op"  0 ""  apply "$TESTROOT/t_file_readonly.dx" --root "$WORKSPACE"
[[ ! -f "$WORKSPACE/should_not_exist.txt" ]] && pass "readonly file not written" || fail "readonly file was written"

# ── 4. dx apply — %%PATCH ────────────────────────────────────────────────────

section "4. dx apply — %%PATCH"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_patch_lines.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="hello.txt"
@@ replace lines=2-2
    This is PATCHED line two.
@@
%%ENDBLOCK

%%END
EOF

check "patch replace lines="          0 "T"  apply "$TESTROOT/t_patch_lines.dx" --root "$WORKSPACE"
grep -q "PATCHED line two" "$WORKSPACE/hello.txt" && pass "patch lines= applied" || fail "patch lines= not applied"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_patch_pattern.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="hello.txt"
@@ replace pattern="line three"
    REPLACED line three.
@@
%%ENDBLOCK

%%END
EOF

check "patch replace pattern="        0 "T"  apply "$TESTROOT/t_patch_pattern.dx" --root "$WORKSPACE"
grep -q "REPLACED line three" "$WORKSPACE/hello.txt" && pass "patch pattern= applied" || fail "patch pattern= not applied"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_patch_insert.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="src/main.cs"
@@ insert after-pattern="Console.WriteLine"
    Console.WriteLine("Inserted line");
@@
%%ENDBLOCK

%%END
EOF

check "patch insert after-pattern"    0 "T"  apply "$TESTROOT/t_patch_insert.dx" --root "$WORKSPACE"
grep -q "Inserted line" "$WORKSPACE/src/main.cs" && pass "insert after-pattern applied" || fail "insert after-pattern failed"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_patch_delete.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="src/main.cs"
@@ delete pattern="// TODO: implement"
@@
%%ENDBLOCK

%%END
EOF

check "patch delete pattern"          0 "T"  apply "$TESTROOT/t_patch_delete.dx" --root "$WORKSPACE"
grep -q "TODO: implement" "$WORKSPACE/src/main.cs" && fail "TODO should be deleted" || pass "patch delete applied"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_patch_badpattern.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%PATCH path="hello.txt"
@@ replace pattern="PATTERN_THAT_DOES_NOT_EXIST_ANYWHERE"
    replacement
@@
%%ENDBLOCK

%%END
EOF

check "patch bad pattern rolls back"   1 ""   apply "$TESTROOT/t_patch_badpattern.dx" --root "$WORKSPACE"
EXPECTED_HEAD="$HEAD"
check "HEAD unchanged after bad patch" 0 "$EXPECTED_HEAD"  snap list --root "$WORKSPACE"

# ── 5. dx apply — %%FS ───────────────────────────────────────────────────────

section "5. dx apply — %%FS"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_fs_move.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%FS op="move" from="newfile.txt" to="moved/newfile.txt"
%%ENDBLOCK

%%END
EOF

check "fs move"                       0 "T"  apply "$TESTROOT/t_fs_move.dx" --root "$WORKSPACE"
[[ -f "$WORKSPACE/moved/newfile.txt" ]]  && pass "file at new location" || fail "file not at new location"
[[ ! -f "$WORKSPACE/newfile.txt" ]]      && pass "original removed"     || fail "original still exists"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_fs_delete.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%FS op="delete" path="moved/newfile.txt"
%%ENDBLOCK

%%END
EOF

check "fs delete"                     0 "T"  apply "$TESTROOT/t_fs_delete.dx" --root "$WORKSPACE"
[[ ! -f "$WORKSPACE/moved/newfile.txt" ]] && pass "file deleted" || fail "file still exists"

HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
cat > "$TESTROOT/t_fs_delete_ifexists.dx" <<EOF
%%DX v1.3 session=test author=llm base=$HEAD

%%FS op="delete" path="totally_nonexistent.txt" if-exists="true"
%%ENDBLOCK

%%END
EOF

check "fs delete if-exists on missing" 0 ""  apply "$TESTROOT/t_fs_delete_ifexists.dx" --root "$WORKSPACE"

# BOM strip
printf '\xef\xbb\xbfBOM content\nLine two\n' > "$WORKSPACE/bom_test.txt"

cat > "$TESTROOT/t_fs_encode.dx" <<'DXEOF'
%%DX v1.3 session=test author=llm

%%FS op="encode" path="bom_test.txt" to="utf-8-no-bom" line-endings="lf"
%%ENDBLOCK

%%END
DXEOF

check "fs encode strips BOM"          0 ""  apply "$TESTROOT/t_fs_encode.dx" --root "$WORKSPACE"
FIRST3=$(od -A n -t x1 -N 3 "$WORKSPACE/bom_test.txt" | tr -d ' \n')
[[ "$FIRST3" != "efbbbf" ]] && pass "BOM stripped" || fail "BOM still present"

# ── 6. base= mismatch ────────────────────────────────────────────────────────

section "6. base= conflict semantics"

CURRENT_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep -oE 'T[0-9]{4}' | tail -1)

cat > "$TESTROOT/t_stale_base.dx" <<'EOF'
%%DX v1.3 session=test author=llm base=T0000

%%FILE path="stale_test.txt"
    Should fail.
%%ENDBLOCK

%%END
EOF

check "stale base= rejected"          3 "mismatch"  apply "$TESTROOT/t_stale_base.dx" --root "$WORKSPACE"
[[ ! -f "$WORKSPACE/stale_test.txt" ]] && pass "tree unchanged after mismatch" || fail "file written despite mismatch"

# ── 7. --dry-run ──────────────────────────────────────────────────────────────

section "7. --dry-run"

cat > "$TESTROOT/t_dryrun.dx" <<EOF
%%DX v1.3 session=test author=llm base=$CURRENT_HEAD

%%FILE path="dryrun_test.txt"
    Should not appear.
%%ENDBLOCK

%%END
EOF

check "dry-run reports dry run"       0 "dry run"       apply "$TESTROOT/t_dryrun.dx" --root "$WORKSPACE" --dry-run
[[ ! -f "$WORKSPACE/dryrun_test.txt" ]] && pass "--dry-run did not write file" || fail "--dry-run wrote file"
check "HEAD unchanged after dry-run"  0 "$CURRENT_HEAD" snap list --root "$WORKSPACE"

# ── 8. apply via stdin ────────────────────────────────────────────────────────

section "8. apply via stdin"

CURRENT_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep -oE 'T[0-9]{4}' | tail -1)

STDIN_DOC="%%DX v1.3 session=test author=llm base=$CURRENT_HEAD

%%FILE path=\"stdin_test.txt\"
    Written via stdin.
%%ENDBLOCK

%%END"

STDIN_OUT=$(echo "$STDIN_DOC" | dotnet run --project "$DX_PROJECT" --no-build -- apply --root "$WORKSPACE" 2>&1) || true
echo "$STDIN_OUT" | grep -qE "T[0-9]{4}" && pass "apply stdin produced snap" || fail "apply stdin failed: $(echo "$STDIN_OUT" | head -2)"
[[ -f "$WORKSPACE/stdin_test.txt" ]] && pass "stdin apply wrote file" || fail "stdin apply did not write file"

CURRENT_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep -oE 'T[0-9]{4}' | tail -1)

# ── 9. snap diff ─────────────────────────────────────────────────────────────

section "9. dx snap diff"

check "snap diff T0000 to current"    0 ""           snap diff T0000 "$CURRENT_HEAD" --root "$WORKSPACE"
check "diff shows added files"        0 "added"      snap diff T0000 "$CURRENT_HEAD" --root "$WORKSPACE"
check "diff same snap = no diffs"     0 "No diff"    snap diff T0000 T0000 --root "$WORKSPACE"
check "diff --path scopes"            0 ""           snap diff T0000 "$CURRENT_HEAD" --root "$WORKSPACE" --path src/

# ── 10. snap checkout ─────────────────────────────────────────────────────────

section "10. dx snap checkout"

grep -q "PATCHED" "$WORKSPACE/hello.txt" \
    && pass "hello.txt is modified before checkout" \
    || skip "hello.txt pre-checkout state check"

check "snap checkout T0000"           0 "Checked out"  snap checkout T0000 --root "$WORKSPACE"

grep -q "Hello, World!" "$WORKSPACE/hello.txt" && pass "checkout restored hello.txt"  || fail "checkout did not restore hello.txt"
grep -q "PATCHED"        "$WORKSPACE/hello.txt" && fail "PATCHED should be gone"       || pass "PATCHED content removed after checkout"

NEW_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')
[[ "$NEW_HEAD" == "T0000" ]] && pass "checkout HEAD is T0000 (idempotent snap reuse)" || fail "checkout HEAD expected T0000, got $NEW_HEAD"
CURRENT_HEAD="$NEW_HEAD"

# ── 11. dx log ───────────────────────────────────────────────────────────────

section "11. dx log"

check "log shows entries"             0 ""     log --root "$WORKSPACE"
check "log shows snap handles"        0 "T00"  log --root "$WORKSPACE"
check "log --limit 2"                 0 ""     log --root "$WORKSPACE" --limit 2

# ── 12. dx session ────────────────────────────────────────────────────────────

section "12. dx session"

check "session list shows sessions"   0 ""               session list --root "$WORKSPACE"
check "session list shows active"     0 "active"         session list --root "$WORKSPACE"
check "session show shows HEAD"       0 "HEAD"           session show --root "$WORKSPACE"
check "session show shows Snaps"      0 "Snaps"          session show --root "$WORKSPACE"
check "session new creates session"   0 "New session"    session new my-new-session --root "$WORKSPACE"
check "session list shows new"        0 "my-new-session" session list --root "$WORKSPACE"
check "session close closes it"       0 "Closed"         session close my-new-session --root "$WORKSPACE"
check "session list shows closed"     0 "closed"         session list --root "$WORKSPACE"

# ── 13. dx pack ───────────────────────────────────────────────────────────────

section "13. dx pack"

PACK_OUT="$TESTROOT/pack_out.dx"

check "pack to stdout"                0 "%%FILE"          pack . --root "$WORKSPACE"
check "pack --out writes file"        0 "Packed"          pack . --root "$WORKSPACE" --out "$PACK_OUT"

[[ -f "$PACK_OUT" ]]                             && pass "pack --out file created"       || fail "pack --out file missing"
grep -q '%%FILE'          "$PACK_OUT"            && pass "pack has %%FILE blocks"        || fail "pack missing %%FILE"
grep -q '%%ENDBLOCK'      "$PACK_OUT"            && pass "pack has %%ENDBLOCK"           || fail "pack missing %%ENDBLOCK"
grep -q 'readonly="true"' "$PACK_OUT"            && pass "pack files marked readonly"   || fail "pack missing readonly"
grep -q '\.dx/'           "$PACK_OUT" 2>/dev/null && fail "pack must not include .dx/"  || pass "pack excludes .dx/"

check "pack --file-type .cs"          0 "Packed"  pack . --root "$WORKSPACE" --file-type .cs --out "$TESTROOT/cs_only.dx"
grep -q "hello.txt" "$TESTROOT/cs_only.dx" 2>/dev/null && fail "--file-type .cs included .txt" || pass "--file-type .cs excludes .txt"
grep -q "main.cs"   "$TESTROOT/cs_only.dx" 2>/dev/null && pass "--file-type .cs includes .cs"  || fail "--file-type .cs missing .cs files"

check "pack --tree"                   0 "Packed"   pack . --root "$WORKSPACE" --tree --out "$TESTROOT/tree.dx"
grep -q "%%NOTE" "$TESTROOT/tree.dx" && pass "pack --tree has %%NOTE block" || fail "pack --tree missing %%NOTE"
check "pack --session-header"         0 "Packed"   pack . --root "$WORKSPACE" --session-header --out "$TESTROOT/hdr.dx"
head -1 "$TESTROOT/hdr.dx" | grep -q "%%DX" && pass "--session-header first line is %%DX" || fail "--session-header missing %%DX"
check "pack --metadata"               0 "Packed"   pack . --root "$WORKSPACE" --metadata --out "$TESTROOT/meta.dx"
grep -q "%%NOTE" "$TESTROOT/meta.dx" && pass "pack --metadata has %%NOTE block" || fail "pack --metadata missing %%NOTE"
check "pack single file"              0 "%%FILE"   pack hello.txt --root "$WORKSPACE"

printf '\x00\x01\x02\x03binary' > "$WORKSPACE/binary.bin"
dx pack . --root "$WORKSPACE" --out "$TESTROOT/nobinary.dx" 2>&1 || true
grep -q "binary.bin" "$TESTROOT/nobinary.dx" 2>/dev/null && fail "pack included binary" || pass "pack excludes binary files"
rm -f "$WORKSPACE/binary.bin"

PACK_BOM=$(od -A n -t x1 -N 3 "$PACK_OUT" | tr -d ' \n')
[[ "$PACK_BOM" != "efbbbf" ]] && pass "pack output is UTF-8 no-BOM" || fail "pack output has BOM"

# ── 14. dx run ───────────────────────────────────────────────────────────────

section "14. dx run"

check "run against HEAD"              0 "Running against"  run "echo hello_from_run" --root "$WORKSPACE"
check "run --snap T0000"              0 ""                  run --snap T0000 "echo ok" --root "$WORKSPACE"
check "run --timeout"                 0 ""                  run --timeout 10 "echo ok" --root "$WORKSPACE"
check "run nonexistent snap fails"    2 ""                  run --snap T9999 "echo x" --root "$WORKSPACE"

RUN_ISOLATION=$(dx run --snap T0000 "ls" --root "$WORKSPACE" 2>&1) || true
echo "$RUN_ISOLATION" | grep -q "stdin_test.txt" \
    && fail "T0000 snap should not contain stdin_test.txt" \
    || pass "snap run is isolated (stdin_test.txt absent in T0000)"

# ── 15. dx eval ──────────────────────────────────────────────────────────────

section "15. dx eval"

CURRENT_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}')

check "eval b-passes PASS"            0 "PASS"  eval T0000 "$CURRENT_HEAD" "echo ok" --root "$WORKSPACE" --pass-if b-passes
check "eval exit-equal PASS"          0 "PASS"  eval T0000 "$CURRENT_HEAD" "echo ok" --root "$WORKSPACE" --pass-if exit-equal
check "eval both-pass PASS"           0 "PASS"  eval T0000 "$CURRENT_HEAD" "echo ok" --root "$WORKSPACE" --pass-if both-pass
check "eval no-regression PASS"       0 "PASS"  eval T0000 "$CURRENT_HEAD" "echo ok" --root "$WORKSPACE" --pass-if no-regression
check "eval b-passes FAIL"            1 "FAIL"  eval T0000 "$CURRENT_HEAD" "exit 1"  --root "$WORKSPACE" --pass-if b-passes
check "eval --label-a --label-b"      0 "PASS"  eval T0000 "$CURRENT_HEAD" "echo ok" --root "$WORKSPACE" --label-a baseline --label-b candidate --pass-if both-pass
check "eval --sequential"             0 "PASS"  eval T0000 "$CURRENT_HEAD" "echo ok" --root "$WORKSPACE" --sequential --pass-if both-pass

# ── 16. dx config ─────────────────────────────────────────────────────────────

section "16. dx config"

check "config set"                    0 "Set"       config set conflict.on_base_mismatch reject --root "$WORKSPACE"
check "config get returns value"      0 "reject"    config get conflict.on_base_mismatch --root "$WORKSPACE"
check "config set run_timeout"        0 "Set"       config set run.run_timeout 60 --root "$WORKSPACE"
check "config list"                   0 "conflict"  config list --root "$WORKSPACE"
check "config show-effective"         0 "Effective" config show-effective --root "$WORKSPACE"
check "config show-effective source"  0 "local"     config show-effective --root "$WORKSPACE"
check "config unset"                  0 "Unset"     config unset conflict.on_base_mismatch --root "$WORKSPACE"
check "config get after unset"        0 "default"   config get conflict.on_base_mismatch --root "$WORKSPACE"
check "config set unknown key fails"  2 "Unknown"   config set unknown.key value --root "$WORKSPACE"
check "snap.exclude global blocked"   2 "globally"  config set --global snap.exclude "[]" --root "$WORKSPACE"

# ── 17. Error handling ────────────────────────────────────────────────────────

section "17. Error handling"

check "snap list no workspace"        2 ""  snap list --root "$TESTROOT/no_such_dir"

OUT=$(dx snap show --root "$WORKSPACE" 2>&1) || true
echo "$OUT" | grep -qiE "handle|required|argument|Missing" \
    && pass "snap show without handle shows guidance" \
    || fail "snap show without handle unhelpful: $(echo "$OUT" | head -2)"

check "apply missing file"            1 ""            apply "$TESTROOT/no_such.dx" --root "$WORKSPACE"

cat > "$TESTROOT/malformed.dx" <<'EOF'
This is not a DX document.
EOF
check "apply malformed doc"           2 "parse error"  apply "$TESTROOT/malformed.dx" --root "$WORKSPACE"

# ── 18. Multi-session isolation ───────────────────────────────────────────────

section "18. Multi-session isolation"

check "workspace2 snap list"          0 "T0000"       snap list --root "$WORKSPACE2"
check "workspace2 own session"        0 "my-session"  session list --root "$WORKSPACE2"

cat > "$TESTROOT/t_ws2.dx" <<'EOF'
%%DX v1.3 session=my-session author=llm base=T0000

%%FILE path="ws2_only.txt"
    Workspace2 only.
%%ENDBLOCK

%%END
EOF

check "apply to workspace2"           0 "T0001"  apply "$TESTROOT/t_ws2.dx" --root "$WORKSPACE2"
[[ ! -f "$WORKSPACE/ws2_only.txt" ]] && pass "workspace2 change isolated" || fail "workspace2 change leaked"

# ── 19. %%REQUEST run gate ────────────────────────────────────────────────────

section "19. %%REQUEST run gate"

CURRENT_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}' | tail -1)

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

check "apply passing run gate"        0 "T"  apply "$TESTROOT/t_gate_pass.dx" --root "$WORKSPACE"
[[ -f "$WORKSPACE/gated.txt" ]] && pass "file created when gate passes" || fail "file missing despite passing gate"

CURRENT_HEAD=$(dx snap list --root "$WORKSPACE" 2>&1 | grep "HEAD" | grep -oE 'T[0-9]{4}' | tail -1)

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

check "apply failing run gate rolls back" 1 ""  apply "$TESTROOT/t_gate_fail.dx" --root "$WORKSPACE"
[[ ! -f "$WORKSPACE/gated_fail.txt" ]] && pass "file rolled back after failing gate" || fail "file persists despite failing gate"

# ── Results ───────────────────────────────────────────────────────────────────

section "Results"

TOTAL=$((PASS + FAIL + SKIP))
echo ""
printf "Tests: %d  " "$TOTAL"
printf "${GREEN}passed: %d${RESET}  " "$PASS"
printf "${RED}failed: %d${RESET}  " "$FAIL"
printf "${YELLOW}skipped: %d${RESET}\n" "$SKIP"

if [[ ${#FAILURES[@]} -gt 0 ]]; then
    echo -e "\n${BOLD}${RED}Failed tests:${RESET}"
    for f in "${FAILURES[@]}"; do
        echo -e "  ${RED}•${RESET} $f"
    done
fi

echo -e "\nTest data: ${CYAN}$TESTROOT${RESET}"
echo "Cleanup:   rm -rf $TESTROOT"

[[ $FAIL -eq 0 ]] \
    && echo -e "\n${BOLD}${GREEN}All tests passed.${RESET}" \
    || echo -e "\n${BOLD}${RED}$FAIL test(s) failed.${RESET}"

[[ $FAIL -eq 0 ]] && exit 0 || exit 1
