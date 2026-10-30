@echo off
setlocal
title AtlasNet Web Server
where py >nul 2>nul
if not errorlevel 1 (
    py -3 -c "import sys; sys.exit(sys.version_info.major != 3)" >nul 2>nul
    if not errorlevel 1 (
        py -3 "%~dp0serve_web.py" %*
        goto finished
    )
)
set "ATLAS_PYTHON=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if exist "%ATLAS_PYTHON%" (
    "%ATLAS_PYTHON%" "%~dp0serve_web.py" %*
    goto finished
)
where python >nul 2>nul
if not errorlevel 1 (
    python "%~dp0serve_web.py" %*
    goto finished
)
echo Python 3 is required. Install Python from https://www.python.org/downloads/
exit /b 1
:finished
if errorlevel 1 (
    echo.
    echo The Web server stopped with an error. See the message above.
    pause
    exit /b 1
)
