:: 1. Собрать образ
docker build -f WebPhone.Api/Dockerfile -t webphoneapi:latest --no-cache .

:: 2. Остановить и удалить старый контейнер (если есть)
docker stop WebPhone.Api 2>nul
docker rm WebPhone.Api 2>nul

:: 3. Запустить новый
docker run -d --name WebPhone.Api ^
  -p 32768:8080 ^
  -e ASPNETCORE_ENVIRONMENT=Development ^
  -e ASPNETCORE_HTTP_PORTS=8080 ^
  -e ASPNETCORE_HTTPS_PORTS= ^
  webphoneapi:latest

echo Done. Container started on port 32768

pause