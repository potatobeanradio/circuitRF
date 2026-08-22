@echo off
rem Windows counterpart of ensure-built.sh -- same contract, same guarantees.
rem
rem THIS SCRIPT MUST NEVER FAIL A BUILD. The worker is optional: circuitRF builds and runs without
rem it, and a design using no compiled device models never notices. Every failure is reported and
rem swallowed, and the exit code stays 0.
rem
rem TWO PRODUCTS, not one -- the reason is in senior_worker.c's header. A Windows model imports its
rem host callbacks from a NAMED MODULE, and an executable's exports are never consulted for that:
rem
rem     crf-model-host.dll   the 15 callbacks, the protocol, and crf_worker_main
rem     senior_worker.exe    a launcher that derives the module name from the model's own import
rem                          table, stages the DLL under it, loads it, and calls in
rem
rem Both are published next to the assemblies, because that is where DeviceWorkerManifest's
rem ToolsDirectory looks -- and the stub finds the DLL beside itself.
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
rem output -- or when either output is missing.
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

rem A COMPILER THAT IS INSTALLED BUT NOT ON PATH is the ordinary way to arrive here, and it looks
rem exactly like having none: PATH is read when a terminal starts, so one installed a minute ago is
rem invisible until a new terminal opens, and a zig unzipped by hand is never on PATH at all. So a
rem compiler can also be named outright, and the message below says so rather than reporting an
rem absence the user knows to be false.
set "zigexe=zig"
if defined CRF_ZIG if exist "%CRF_ZIG%" set "zigexe=%CRF_ZIG%"

if not "%zigexe%"=="zig" goto build
where zig    >nul 2>&1 && goto build
where gcc    >nul 2>&1 && goto build
where docker >nul 2>&1 && goto build
where podman >nul 2>&1 && goto build

echo senior-worker: no C toolchain found on PATH (zig, an MSYS2/MinGW gcc, docker or podman); skipping the device worker.
echo senior-worker: if you just installed one, open a NEW terminal -- PATH is read when a terminal starts.
echo senior-worker: or name it outright:  set CRF_ZIG=C:\path\to\zig.exe
echo senior-worker: circuitRF runs normally; compiled device models need it.
exit /b 0

:build
echo senior-worker: building crf-model-host.dll and senior_worker.exe
rem
rem The output directory, which is NOT in the repository -- it holds build products only, so a fresh
rem checkout does not have one. build.sh makes its own; the direct-compiler paths below never did,
rem and a compiler asked to write into a directory that is not there fails with an error about the
rem OUTPUT rather than about anything wrong with the build.
if not exist "%build%" mkdir "%build%" >nul 2>&1
rem
rem THE TARGET IS x86-64 EVEN ON AN ARM64 MACHINE, and that is not a leftover. This worker exists to
rem LOAD a vendor's compiled model library into its own process, and those ship as x64; a process
rem holds exactly one instruction set, so an arm64 worker could not load one. Windows runs the x64
rem pair (the stub and the callback DLL) under its own translation, which is what makes an ARM
rem machine able to use such a kit at all.
rem
rem build.sh knows every toolchain -- zig, docker/podman, mingw-w64 -- and states that target
rem explicitly, so it is preferred whenever there is a bash to run it with. Without one, drive a
rem compiler directly. THE CHOICE ABOVE ACCEPTS FOUR TOOLCHAINS AND THIS ONCE TRIED ONLY gcc: a
rem machine with zig and no bash decided to build, ran a compiler it did not have, and reported the
rem worker as having failed to build rather than as never having been attempted.
if not "%zigexe%"=="zig" goto viazig
where bash >nul 2>&1 && goto viabash
where zig  >nul 2>&1 && goto viazig
where gcc  >nul 2>&1 && goto viagcc
echo senior-worker: the only toolchain found is a container engine, and driving one needs bash (Git for Windows ships one); skipping the device worker.
echo senior-worker: circuitRF runs normally; compiled device models need it.
exit /b 0

:viabash
bash "%here%build.sh" windows >"%TEMP%\crf-senior-worker-build.log" 2>&1
goto built

:viazig
rem The one direct path that can still be told which target to build for.
"%zigexe%" cc -target x86_64-windows-gnu -O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DCRF_HOST_DLL -shared ^
    "%here%senior_worker.c" "%here%crf-model-host.def" -o "%dll%" -lm ^
    >"%TEMP%\crf-senior-worker-build.log" 2>&1
"%zigexe%" cc -target x86_64-windows-gnu -O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DCRF_HOST_STUB ^
    "%here%senior_worker.c" -o "%exe%" -lshell32 ^
    >>"%TEMP%\crf-senior-worker-build.log" 2>&1
goto built

:viagcc
rem A host gcc cannot be retargeted, so one that builds for arm64 is refused rather than used. It
rem would produce a worker that starts and then cannot load a single model -- a worse place to end
rem up than having no worker, because nothing about that reads as an architecture problem.
set "gccmachine="
for /f "delims=" %%M in ('gcc -dumpmachine 2^>nul') do set "gccmachine=%%M"
echo %gccmachine% | findstr /I /C:"aarch64" /C:"arm64" >nul
if not errorlevel 1 (
    echo senior-worker: the gcc on PATH builds for %gccmachine%, and the worker must be x86-64 to load a model library; skipping the device worker.
    echo senior-worker: install zig, or an x86-64 mingw-w64 gcc, to build it.
    exit /b 0
)
rem The same two commands build.sh would run.
gcc -O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DCRF_HOST_DLL -shared ^
    "%here%senior_worker.c" "%here%crf-model-host.def" -o "%dll%" -lm ^
    >"%TEMP%\crf-senior-worker-build.log" 2>&1
gcc -O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DCRF_HOST_STUB ^
    "%here%senior_worker.c" -o "%exe%" -lshell32 ^
    >>"%TEMP%\crf-senior-worker-build.log" 2>&1
goto built

:built
if not exist "%dll%" goto buildfailed
if not exist "%exe%" goto buildfailed
goto publish

:buildfailed
echo senior-worker: the device worker did not build; the compiler's own output is here:
echo senior-worker:   %TEMP%\crf-senior-worker-build.log
echo senior-worker: circuitRF will run normally, but compiled device models will not be available.
exit /b 0

:publish
if "%dest%"=="" exit /b 0
if not exist "%dest%" mkdir "%dest%" >nul 2>&1
if exist "%dll%" copy /Y "%dll%" "%dest%\" >nul 2>&1
if exist "%exe%" copy /Y "%exe%" "%dest%\" >nul 2>&1
exit /b 0
