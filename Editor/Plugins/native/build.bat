@echo off
setlocal
rem Build into a separate output directory; compare the result with the shipped DLL.
if "%~1"=="" (
    echo Usage: build.bat output-directory
    exit /b 1
)
python "%~dp0build.py" --output "%~1"
exit /b %ERRORLEVEL%
