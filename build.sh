#!/usr/bin/env bash
# FAKE build entry point. Runs from the repo root regardless of where it is
# invoked, then forwards any target name / args to the build project.
#   ./build.sh            # Clean + Test + Plugin (Default)
#   ./build.sh Plugin     # publish both binaries + assemble dist/roslyn-lsp-hook (+ -vscode)
#   RID=linux-x64 ./build.sh Plugin
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec dotnet run --project build/build.fsproj -- "$@"
