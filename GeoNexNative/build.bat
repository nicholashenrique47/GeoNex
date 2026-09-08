@echo off
setlocal
set "GEONEX_CONFIG=%~1"
if not defined GEONEX_CONFIG set "GEONEX_CONFIG=Release"
if /I "%GEONEX_CONFIG%"=="Release" goto configuration_ok
if /I "%GEONEX_CONFIG%"=="Debug" goto configuration_ok
echo Usage: build.bat [Release^|Debug]
exit /b 2

:configuration_ok
set "GEONEX_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%GEONEX_VSWHERE%" (
    echo ERROR: vswhere.exe not found. Install Visual Studio C++ x64 build tools.
    exit /b 1
)
for /f "usebackq tokens=*" %%I in (`"%GEONEX_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "GEONEX_VS=%%I"
if not defined GEONEX_VS (
    echo ERROR: No Visual Studio installation with C++ x64 build tools was found.
    exit /b 1
)
call "%GEONEX_VS%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
pushd "%~dp0"
if errorlevel 1 exit /b 1

rem Keep ISA and FP choices reproducible even in a customized developer shell.
set "CL="
set "_CL_="
set "LINK="
set "_LINK_="
set "GEONEX_OBJ=obj\%GEONEX_CONFIG%"
set "GEONEX_BIN=..\x64\%GEONEX_CONFIG%"
if not exist "%GEONEX_OBJ%" mkdir "%GEONEX_OBJ%"
if not exist "%GEONEX_BIN%" mkdir "%GEONEX_BIN%"
set "GEONEX_COMMON=/nologo /std:c++20 /permissive- /EHsc /W3 /fp:precise /Gy /Gw"
set "GEONEX_OPT=/O2 /Oi /MD /DNDEBUG"
if /I "%GEONEX_CONFIG%"=="Debug" set "GEONEX_OPT=/Od /Zi /MDd /D_DEBUG"
echo GeoNex native %GEONEX_CONFIG% x64: baseline SSE2, isolated AVX2, precise FP, no LTCG.

rem No /GL or /LTCG: the linker must not inline AVX2 into baseline functions.
cl.exe %GEONEX_COMMON% %GEONEX_OPT% /Bv /arch:SSE2 /c GeoNexNative.cpp /Fo"%GEONEX_OBJ%\GeoNexNative.obj" /Fd"%GEONEX_OBJ%\GeoNexNative.pdb"
if errorlevel 1 goto failed
cl.exe %GEONEX_COMMON% %GEONEX_OPT% /arch:SSE2 /c NativeCpu.cpp /Fo"%GEONEX_OBJ%\NativeCpu.obj" /Fd"%GEONEX_OBJ%\NativeCpu.pdb"
if errorlevel 1 goto failed
cl.exe %GEONEX_COMMON% %GEONEX_OPT% /arch:AVX2 /c ParseAvx2.cpp /Fo"%GEONEX_OBJ%\ParseAvx2.obj" /Fd"%GEONEX_OBJ%\ParseAvx2.pdb"
if errorlevel 1 goto failed
link.exe /nologo /DLL /OPT:REF /OPT:ICF /OUT:"%GEONEX_BIN%\GeoNexNative.dll" /IMPLIB:"%GEONEX_BIN%\GeoNexNative.lib" /PDB:"%GEONEX_BIN%\GeoNexNative.pdb" "%GEONEX_OBJ%\GeoNexNative.obj" "%GEONEX_OBJ%\NativeCpu.obj" "%GEONEX_OBJ%\ParseAvx2.obj"
if errorlevel 1 goto failed

rem Fail the build before packaging an ABI-incomplete or stale native binary.
dumpbin.exe /nologo /exports "%GEONEX_BIN%\GeoNexNative.dll" > "%GEONEX_OBJ%\GeoNexNative.exports.txt"
if errorlevel 1 goto failed
for %%E in (GetGeoNexNativeAbiVersion CreateRenderCancellation CancelRender DestroyRenderCancellation ProcessShapeBatchV4 CreateShapeSpatialIndex QueryShapeSpatialIndex) do (
    findstr /R /C:"[ ]%%E$" "%GEONEX_OBJ%\GeoNexNative.exports.txt" >nul
    if errorlevel 1 (
        echo ERROR: Required export %%E is missing from %GEONEX_BIN%\GeoNexNative.dll.
        goto failed
    )
)

rem Build the small legacy geometry DLL required by the desktop application too.
cl.exe %GEONEX_COMMON% %GEONEX_OPT% /arch:SSE2 /LD "..\GeoNex.Core\GeoEngine.cpp" /Fo"%GEONEX_OBJ%\GeoEngine.obj" /Fd"%GEONEX_OBJ%\GeoEngine.pdb" /link /OPT:REF /OPT:ICF /OUT:"%GEONEX_BIN%\GeoNex.Core.dll" /IMPLIB:"%GEONEX_BIN%\GeoNex.Core.lib" /PDB:"%GEONEX_BIN%\GeoNex.Core.pdb"
if errorlevel 1 goto failed
popd
exit /b 0

:failed
popd
exit /b 1
