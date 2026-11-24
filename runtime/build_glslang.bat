@echo off
rem Builds ssbu_glslang.dll (vk_glslang_shim.c + the glslang 16.6.0 release libraries) with MSVC. Run from the source folder
rem (scripts\setup.py does). GLSLANG_ROOT = the folder of the glslang release (include\ and lib\); default ..\reference\upstream\glslang-16.6.0.
rem See plan\phases\V1_vulkan.md section 4 for why this is a DLL and not linked into the exe.
setlocal
if "%GLSLANG_ROOT%"=="" set GLSLANG_ROOT=..\reference\upstream\glslang-16.6.0
set GL=%GLSLANG_ROOT%
set VSW=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
set VSDIR=
if exist "%VSW%" "%VSW%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > "%TEMP%\ssbu_vsdir.txt"
if exist "%TEMP%\ssbu_vsdir.txt" set /p VSDIR=<"%TEMP%\ssbu_vsdir.txt"
if "%VSDIR%"=="" ( echo build_glslang: no Visual Studio / Build Tools with the C++ x64 tools found & exit /b 1 )
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not exist obj_host mkdir obj_host
rem The C runtime the shim is built with must be the one of the libraries. The official release libraries are /MD: the DLL
rem then needs the Visual C++ Redistributable (MSVCP140 / VCRUNTIME140*), which many players lack (Arena 0.2.64/0.2.65:
rem "ssbu_glslang.dll cannot be loaded"). scripts\build_glslang_static.py builds them /MT (deps\glslang-mt): the DLL then
rem imports only KERNEL32 and the Universal CRT of Windows 10/11. GLSLANG_CRT=MD / MT forces one (default: what the libraries are).
set CRT=%GLSLANG_CRT%
if "%CRT%"=="" ( findstr /m /c:"RuntimeLibrary=MT_StaticRelease" "%GL%\lib\glslang.lib" >nul 2>&1 && set "CRT=MT" )
if "%CRT%"=="" set "CRT=MD"
echo build_glslang: /%CRT% (%GL%)
cl /nologo /%CRT% /O2 /W3 /LD /I "%GL%\include" vk_glslang_shim.c /Fo:obj_host\vk_glslang_shim.obj /Fe:ssbu_glslang.dll /link /LIBPATH:"%GL%\lib" glslang.lib glslang-default-resource-limits.lib SPIRV-Tools-opt.lib SPIRV-Tools.lib
exit /b %errorlevel%
