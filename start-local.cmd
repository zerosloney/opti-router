@echo off
rem OptiRouter local launcher (run detached, survives agent session recycling).
rem The app writes publish\logs\service.log itself (TimestampedFileLoggerProvider, exclusive append).
rem Console output must NOT redirect to service.log - cmd's >> holds a deny-write handle
rem that makes the app's own log open fail at startup. Capture console to boot.log instead.
cd /d "%~dp0publish"
set ASPNETCORE_ENVIRONMENT=Production
if not exist logs mkdir logs
OptiRouter.exe >> logs\boot.log 2>&1
