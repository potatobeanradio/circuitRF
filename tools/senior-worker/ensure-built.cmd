@echo off
rem Windows counterpart of ensure-built.sh — same contract, same guarantees.
rem
rem THIS SCRIPT MUST NEVER FAIL A BUILD. The worker is optional: circuitRF builds and runs without
rem it, and a design using no compiled device models never notices. Every failure is reported and
rem swallowed, and the exit code stays 0.
rem
rem TWO PRODUCTS, not one — the reason is in senior_worker.c's header. A Windows model imports its
rem host callbacks from a NAMED MODULE, and an executable's exports are never consulted for that:
rem
rem     crf-model-host.dll   the 15 callbacks, the protocol, and crf_worker_main
rem     senior_worker.exe    a launcher that derives the module name from the model's own import
rem                          table, stages the DLL under it, loads it, and calls in
rem
rem Both are published next to the assemblies, because that is where DeviceWorkerManifest's
rem ToolsDirectory looks — and the stub finds the DLL beside itself.
setlocal
set "here=%~dp0"
set "build=%here%build"
set "dll=%build%\crf-model-host.dll"
set "exe=%build%\senior_worker.exe"
set "dest="

:args
if "%~1"=="" goto after
if /I "%~1"=="--dest" set "dest=%~2" & shift & shift & goto args
shift
goto args
:after

rem Staleness: rebuild when the source, the export list or the build script is newer than either
rem output — or when either output is missing.
set "stale=1"
if exist "%dll%" if exist "%exe%" (
    set "stale=0"
    for %%F in ("%here%senior_worker.c" "%here%crf-model-host.def" "%here%build.sh") do (
        for /f %%R in ('powershell -NoProfile -Command ^
            "if ((Get-Item '%%~fF').LastWriteTime -gt (Get-Item '%dll%').LastWriteTime -or (Get-Item '%%~fF').LastWriteTime -gt (Get-Item '%exe%').LastWriteTime) {1} else {0}" 2^>nul') do (
            if "%%R"=="1" set "stale=1"
        )
    )
)

if "%stale%"=="0" goto publish

where zig    >nul 2>&1 && goto build
where gcc    >nul 2>&1 && goto build
where docker >nul 2>&1 && goto build
where podman >nul 2>&1 && goto build

echo senior-worker: no C toolchain found (zig, an MSYS2/MinGW gcc, docker or podman); skipping the device worker.
echo senior-worker: circuitRF runs normally; compiled device models need it.
exit /b 0

:build
echo senior-worker: building crf-model-host.dll and senior_worker.exe
where bash >nul 2>&1 && (
    bash "%here%build.sh" windows >"%TEMP%\crf-senior-worker-build.log" 2>&1
) || (
    rem No bash — drive the compiler directly, the same two commands build.sh would run.
    gcc -O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DCRF_HOST_DLL -shared ^
        "%here%senior_worker.c" "%here%crf-model-host.def" -o "%dll%" -lm ^
        >"%TEMP%\crf-senior-worker-build.log" 2>&1
    gcc -O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DCRF_HOST_STUB ^
        "%here%senior_worker.c" -o "%exe%" -lshell32 ^
        >>"%TEMP%\crf-senior-worker-build.log" 2>&1
)

if not exist "%dll%" goto buildfailed
if not exist "%exe%" goto buildfailed
goto publish

:buildfailed
echo senior-worker: the device worker did not build; see %TEMP%\crf-senior-worker-build.log
echo senior-worker: circuitRF will run normally, but compiled device models will not be available.
exit /b 0

:publish
if "%dest%"=="" exit /b 0
if not exist "%dest%" mkdir "%dest%" >nul 2>&1
if exist "%dll%" copy /Y "%dll%" "%dest%\" >nul 2>&1
if exist "%exe%" copy /Y "%exe%" "%dest%\" >nul 2>&1
exit /b 0
