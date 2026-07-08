FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем csproj-файлы всех зависимых проектов для кеширования restore
COPY WebPhone.Web/WebPhone.Web.csproj WebPhone.Web/
COPY WebPhone.Contract/WebPhone.Domain.csproj WebPhone.Contract/
COPY WebPhone/WebPhone.UI.csproj WebPhone/
COPY WebPhone.Core/WebPhone.csproj WebPhone.Core/
COPY EverModern.Blazor.DirectCommunication/EverModern.Blazor.DirectCommunication.csproj EverModern.Blazor.DirectCommunication/

RUN dotnet restore "WebPhone.Web/WebPhone.Web.csproj"

# Копируем всё и собираем
COPY . .
WORKDIR "/src/WebPhone.Web"
RUN dotnet publish "WebPhone.Web.csproj" -c Release -o /app/publish --no-restore

# Финальный образ — nginx со статикой
FROM nginx:alpine AS final
COPY WebPhone.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 80
