param([int]$Count = 150)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = (Get-ChildItem "$PSScriptRoot/toolchain/*/bin/x86_64-w64-mingw32-clang++.exe" | Select-Object -First 1).FullName
& $compiler -std=c++20 -O2 -static -municode -o "$root/artifacts/native/ShellSmoke.exe" "$root/tests/ShellSmoke.cpp" -lole32 -lshell32 -luuid
if ($LASTEXITCODE) { throw 'Shell test build failed' }
$dir = "$root/artifacts/shell-files"
New-Item -ItemType Directory -Force $dir | Out-Null
$files = @(0..($Count-1) | ForEach-Object { $path = Join-Path $dir "file 日本語 & $_.txt"; [IO.File]::WriteAllText($path,'Disposable shell test resource'); $path })
$longdir=Join-Path $dir ('a'*160)
$longdir=Join-Path $longdir ('b'*100)
New-Item -ItemType Directory -Force $longdir | Out-Null
$longfile=Join-Path $longdir 'long 日本語 file.txt'
[IO.File]::WriteAllText($longfile,'Long-path shell test resource')
$files += $longfile
$env:LOCKPEEK_TEST_REPORT = "$root/artifacts/shell-report.json"
if (Test-Path -LiteralPath $env:LOCKPEEK_TEST_REPORT) { Remove-Item -LiteralPath $env:LOCKPEEK_TEST_REPORT }
$before = @(Get-Process LockPeek -ErrorAction SilentlyContinue)
$watch = [Diagnostics.Stopwatch]::StartNew()
& "$root/artifacts/native/ShellSmoke.exe" "$root/artifacts/package/LockPeek.Shell.dll" @files
if ($LASTEXITCODE) { throw "Native COM shell test failed ($LASTEXITCODE)" }
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (!(Test-Path $env:LOCKPEEK_TEST_REPORT) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (!(Test-Path $env:LOCKPEEK_TEST_REPORT)) { throw 'No app receipt/scan report' }
$report = Get-Content $env:LOCKPEEK_TEST_REPORT -Raw | ConvertFrom-Json
$watch.Stop()
if ($report.Paths.Count -ne $files.Count -or $report.Scan.Covered -lt $Count) { throw 'Lost paths or incomplete ordinary-file coverage' }
$received = @($report.Paths | ForEach-Object { if ($_.StartsWith('\\?\')) { $_.Substring(4) } else { $_ } })
if (Compare-Object $files $received) { throw 'Transferred paths do not match' }
$handoffDeadline = [DateTime]::UtcNow.AddSeconds(3)
do { $running = @(Get-Process LockPeek -ErrorAction SilentlyContinue); if ($running.Count -le 1) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $handoffDeadline)
if ($running.Count -ne 1) { throw "Expected one app process; found $($running.Count)" }
if ($before.Count -eq 1 -and $before[0].Id -ne $report.Pid) { throw 'Existing window was not reused' }
"PASS native COM IShellItemArray -> inherited pipe -> one WinUI window -> $($report.Paths.Count) exact paths received, $($report.Scan.Covered) covered"
"App working set: $([Math]::Round($report.WorkingSet/1MB,1)) MiB; scan: $($report.Scan.Elapsed)"
"Invocation to scan report: $($watch.ElapsedMilliseconds) ms; PID $($report.Pid); HWND $($report.WindowHandle)"
Remove-Item Env:LOCKPEEK_TEST_REPORT
