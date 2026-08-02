@echo off
REM Double-click launcher. Builds if needed, starts the app, opens your browser, and closes this
REM window when you close the page.
REM
REM Release rather than Debug: this is the "use it" path rather than the "work on it" path, and
REM the first run pays for a rebuild only once.
cd /d "%~dp0"
dotnet run --project RAG -c Release --launch-profile http
