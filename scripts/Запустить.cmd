@echo off
cd /d "%~dp0"
echo Open http://localhost:5080 in your browser after the server starts.
echo Close this window to stop the server.
TaskPlanner.exe --urls http://localhost:5080
pause
