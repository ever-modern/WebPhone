@echo off
setlocal enabledelayedexpansion
title WebPhone Deploy

set "SELF=%~dp0"
:: убираем завершающий backslash, чтобы `"%SELF%"` не экранировал кавычку
set "SELF=%SELF:~0,-1%"

set COMPOSE_FILE=C:\ProgramData\Sites\docker-compose.yml
set COMPOSE_BACKUP=C:\ProgramData\Sites\docker-compose.yml.bak

:: ──────────── 1. Тег образа ────────────
set /p TAG="Image tag [latest]: "
if "%TAG%"=="" set TAG=latest

:: ──────────── 2. Сборка API ────────────
echo.
echo ===== Building WebPhone.Api :%TAG% =====
docker build -f "%SELF%\WebPhone.Api\Dockerfile" -t "webphoneapi:%TAG%" --no-cache "%SELF%"
if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: API build failed.
    pause
    exit /b 1
)
docker tag "webphoneapi:%TAG%" webphoneapi:latest

:: ──────────── 3. Сборка Web ────────────
echo.
echo ===== Building WebPhone.Web :%TAG% =====
docker build -f "%SELF%\WebPhone.Web\Dockerfile" -t "webphoneweb:%TAG%" --no-cache "%SELF%"
if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Web build failed.
    pause
    exit /b 1
)
docker tag "webphoneweb:%TAG%" webphoneweb:latest

:: ──────────── 4. Переключение docker-compose: build → image ────────────
:: Ищем "    build:" под "  web-phone:" — если есть build:, меняем на image:
findstr /c:"    build:" "%COMPOSE_FILE%" >nul 2>&1
if !ERRORLEVEL! equ 0 (
    echo.
    echo docker-compose.yml использует build: для web-phone.
    echo Переключаю на image: webphoneweb:latest ^(бекап: %COMPOSE_BACKUP%^)
    copy /y "%COMPOSE_FILE%" "%COMPOSE_BACKUP%" >nul
    powershell -Command "$f='%COMPOSE_FILE%'; $c=[System.IO.File]::ReadAllText($f); $nl=[char]13+[char]10; $c=$c -replace '(?s)  web-phone:\r?\n    build:\r?\n      context:[^\r\n]*\r?\n      dockerfile:[^\r\n]*\r?\n', ('  web-phone:'+$nl+'    image: webphoneweb:latest'+$nl); [System.IO.File]::WriteAllText($f, $c)"
    if !ERRORLEVEL! neq 0 (
        echo.
        echo ERROR: Failed to update docker-compose.yml. Restoring backup...
        copy /y "%COMPOSE_BACKUP%" "%COMPOSE_FILE%" >nul
        pause
        exit /b 1
    )
    echo OK.
)

:: ──────────── 5. Подтверждение ────────────
echo.
echo ===== Images built =====
echo   webphoneapi:%TAG%  ^(tagged :latest^)
echo   webphoneweb:%TAG%  ^(tagged :latest^)
echo.
set /p DEPLOY="Deploy to Docker? (yes/no): "
if /i not "%DEPLOY%"=="yes" (
    echo.
    echo Skipped. Images are ready for manual deploy.
    pause
    exit /b 0
)

:: ──────────── 6. Деплой ────────────
echo.
echo ===== Deploying to Docker =====
pushd C:\ProgramData\Sites
docker compose up -d --force-recreate web-phone-api web-phone
if %ERRORLEVEL% neq 0 (
    popd
    echo.
    echo ERROR: Deploy failed. Restoring docker-compose.yml backup...
    if exist "%COMPOSE_BACKUP%" copy /y "%COMPOSE_BACKUP%" "%COMPOSE_FILE%" >nul
    pause
    exit /b 1
)
popd

:: ──────────── 7. Reload nginx ────────────
docker exec nginx-proxy nginx -s reload
if %ERRORLEVEL% neq 0 (
    echo WARNING: nginx reload failed ^(container might be restarting^)
) else (
    echo nginx reloaded.
)

:: ──────────── 8. Готово ────────────
echo.
echo ===== Done =====
echo   API:  webphoneapi:%TAG%
echo   Web:  webphoneweb:%TAG%
echo   URL:  https://web-phone-api.enjoyer-station.myvnc.com
echo   URL:  https://web-phone.enjoyer-station.myvnc.com
echo.
pause
