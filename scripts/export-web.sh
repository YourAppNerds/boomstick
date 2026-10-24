#!/usr/bin/env bash
# Builds the C# solution and exports the release nothreads Web build to build/web.
# Needs the custom editor built from YAN-kyle/godot (branch web-dotnet-rebased).
# Override the editor path with GODOT=/path/to/godot.windows.editor.x86_64.mono.console.exe.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="${GODOT:-/d/source/repos/godot-web-dotnet/godot-wt/rebase/bin/godot.windows.editor.x86_64.mono.console.exe}"
OUT="$ROOT/build/web"
LOG="$ROOT/build/export-web.log"

if [ ! -x "$GODOT" ]; then
    echo "Godot editor not found at $GODOT (set GODOT=...)" >&2
    exit 1
fi

mkdir -p "$OUT"
# Keep Godot from importing (and exporting) anything under build/: logs, screenshots, movie frames.
touch "$ROOT/build/.gdignore"
rm -f "$OUT"/*

echo "== import + build solutions"
# On a fresh checkout the import runs before the C# assembly exists and logs "Failed to create an
# autoload ... is not compiling". That is expected, so only the export step below is checked.
"$GODOT" --headless --path "$ROOT" --import > "$LOG" 2>&1
"$GODOT" --headless --path "$ROOT" --build-solutions --quit >> "$LOG" 2>&1
export_from=$(( $(wc -l < "$LOG") + 1 ))

echo "== export release (Web, nothreads)"
start=$(date +%s)
"$GODOT" --headless --path "$ROOT" --export-release "Web" "$OUT/index.html" >> "$LOG" 2>&1
echo "export took $(( $(date +%s) - start ))s, log: $LOG"
if tail -n "+$export_from" "$LOG" | grep -E "ERROR|SCRIPT ERROR"; then
    echo "export log contains errors, failing" >&2
    exit 1
fi
if [ ! -f "$OUT/index.wasm" ] || [ ! -f "$OUT/index.pck" ]; then
    echo "export failed: index.wasm or index.pck missing" >&2
    exit 1
fi

echo "== sizes"
total=0
for f in "$OUT"/*; do
    sz=$(stat -c %s "$f")
    total=$((total + sz))
    gz=""
    case "$f" in
        *.wasm|*.pck|*.js) gz="  (gzip -9: $(gzip -9 -c "$f" | wc -c) bytes)" ;;
    esac
    printf "%12d  %s%s\n" "$sz" "$(basename "$f")" "$gz"
done
printf "%12d  total\n" "$total"
echo "Serve with: python -m http.server -d \"$OUT\" 8000  (no COOP/COEP headers needed)"
