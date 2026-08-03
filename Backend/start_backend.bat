@echo off
cd /d "%~dp0"
echo Starting Object Spawning backend on port 8000...
echo Press Ctrl+C, or just close this window, to stop it.
echo.
.venv\Scripts\python.exe -m uvicorn main:app --host 0.0.0.0 --port 8000
pause
