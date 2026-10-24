#!/usr/bin/env bash
# Prepares a GitHub Pages deploy of the Web build on the local gh-pages branch.
#
#   scripts/deploy-pages.sh [--skip-export] [--push]
#
# 1. Exports the release nothreads Web build (scripts/export-web.sh) unless --skip-export.
# 2. Checks out gh-pages (created as an orphan branch the first time) in a temporary git worktree,
#    replaces its contents with build/web plus an empty .nojekyll, and commits.
# 3. Prints the push command. Nothing leaves this machine unless you pass --push.
#
# The build has no threads, so it needs no COOP/COEP headers and runs on plain GitHub Pages.
# Environment: REMOTE (default origin), BRANCH (default gh-pages), GODOT (see export-web.sh),
# COMMIT_TRAILER (optional extra lines for the deploy commit message).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REMOTE="${REMOTE:-origin}"
BRANCH="${BRANCH:-gh-pages}"
EXPORT=1
PUSH=0
for arg in "$@"; do
    case "$arg" in
        --skip-export) EXPORT=0 ;;
        --push) PUSH=1 ;;
        *) echo "unknown argument: $arg" >&2; exit 2 ;;
    esac
done

cd "$ROOT"
if [ "$EXPORT" = 1 ]; then
    bash scripts/export-web.sh
fi
for f in index.html index.js index.wasm index.pck; do
    if [ ! -f "build/web/$f" ]; then
        echo "build/web/$f is missing; run scripts/export-web.sh first" >&2
        exit 1
    fi
done

SRC_SHA="$(git rev-parse --short HEAD)"
DIRTY=""
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    DIRTY=" (with uncommitted changes)"
fi

TMP="$(mktemp -d)"
WT="$TMP/$BRANCH"
cleanup() {
    git -C "$ROOT" worktree remove --force "$WT" >/dev/null 2>&1 || true
    rm -rf "$TMP"
}
trap cleanup EXIT

if git show-ref --verify --quiet "refs/heads/$BRANCH"; then
    git worktree add --quiet "$WT" "$BRANCH"
else
    echo "== creating orphan branch $BRANCH"
    git worktree add --quiet --detach "$WT"
    git -C "$WT" checkout --quiet --orphan "$BRANCH"
    git -C "$WT" rm -r -q -f --cached . >/dev/null 2>&1 || true
fi

# Replace everything in the pages tree with the fresh build.
find "$WT" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
cp build/web/* "$WT/"
touch "$WT/.nojekyll"
# Store the build byte for byte: no line-ending conversion of index.js or index.html.
echo '* -text' > "$WT/.gitattributes"

git -C "$WT" add -A .
if git -C "$WT" diff --cached --quiet; then
    echo "== $BRANCH already matches build/web, nothing to commit"
else
    MSG="Deploy Boomstick web build from $SRC_SHA$DIRTY"
    if [ -n "${COMMIT_TRAILER:-}" ]; then
        MSG="$MSG"$'\n\n'"$COMMIT_TRAILER"
    fi
    git -C "$WT" commit --quiet -m "$MSG"
    echo "== committed $(git -C "$WT" rev-parse --short HEAD) on $BRANCH"
fi
git -C "$WT" ls-tree -r --long HEAD | awk '{ printf "%12s  %s\n", $4, $5 }'

PUSH_CMD="git -C \"$ROOT\" push $REMOTE $BRANCH"
if [ "$PUSH" = 1 ]; then
    echo "== pushing"
    git push "$REMOTE" "$BRANCH"
else
    echo
    echo "Not pushed. To publish, run:"
    echo "  $PUSH_CMD"
    echo "and set GitHub Pages to deploy from the $BRANCH branch (root folder)."
fi
