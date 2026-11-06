param([string]$Compiler)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Compiler) { $Compiler = (Get-ChildItem "$PSScriptRoot/toolchain/*/bin/x86_64-w64-mingw32-clang++.exe" | Select-Object -First 1).FullName }
if (!$Compiler) { throw 'Pass -Compiler with the path to LLVM-MinGW clang++ (x64).' }
New-Item -ItemType Directory -Force "$root/artifacts/native" | Out-Null
& $Compiler -std=c++20 -O2 -Wall -Wextra -Werror -shared -static -o "$root/artifacts/native/LockPeek.Shell.dll" "$root/src/LockPeek.Shell/LockPeek.Shell.cpp" "$root/src/LockPeek.Shell/LockPeek.Shell.def" -lole32 -lshell32 -lshlwapi -luuid -ladvapi32
if ($LASTEXITCODE) { throw 'Native shell build failed' }
