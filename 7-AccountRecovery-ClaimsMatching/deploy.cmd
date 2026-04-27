@echo off
setlocal enabledelayedexpansion

echo.
echo ========================================
echo Azure Functions Custom Deploy Script
echo ========================================
echo.

:: Prerequisites
SET ARTIFACTS=%~dp0%..\artifacts
SET PROJECT_DIR=%~dp0

IF NOT DEFINED DEPLOYMENT_SOURCE (
  SET DEPLOYMENT_SOURCE=%~dp0%.
)

IF NOT DEFINED DEPLOYMENT_TARGET (
  SET DEPLOYMENT_TARGET=%ARTIFACTS%\wwwroot
)

:: 1. Restore & Publish
echo Publishing project...
dotnet publish "%PROJECT_DIR%account-recovery-claim-matching.csproj" ^
  --configuration Release ^
  --output "%DEPLOYMENT_TARGET%" ^
  /p:GenerateFullPaths=true

IF !ERRORLEVEL! NEQ 0 goto error

echo.
echo Deployment successful.
goto end

:error
echo.
echo Deployment failed with error level !ERRORLEVEL!.
exit /b 1

:end
echo Done.
exit /b 0
