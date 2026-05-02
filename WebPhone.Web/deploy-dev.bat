@echo off
pushd "%~dp0"
powershell -ExecutionPolicy Bypass -File "deploy-netlify.ps1"
popd
pause