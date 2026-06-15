@echo off
rem FAKE build entry point. Runs from the repo root and forwards target/args.
rem   build.cmd            Clean + Test + Plugin (Default)
rem   build.cmd Plugin     publish both binaries + assemble dist\roslyn-lsp-hook
setlocal
cd /d "%~dp0"
dotnet run --project build\build.fsproj -- %*
endlocal
