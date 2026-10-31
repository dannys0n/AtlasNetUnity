@echo off
setlocal
title AtlasNet Soccer Web Server
set "PYTHON_EXE=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
where py >nul 2>nul
if not errorlevel 1 (
    py -3 "%~dp0serve_web.py" %*
    goto finished
)
if exist "%PYTHON_EXE%" (
    "%PYTHON_EXE%" "%~dp0serve_web.py" %*
    goto finished
)
python "%~dp0serve_web.py" %*
:finished
if errorlevel 1 pause
