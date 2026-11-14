FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["VaniFine.WebApi.csproj", "./"]
RUN dotnet restore "VaniFine.WebApi.csproj"

COPY . .
RUN dotnet publish "VaniFine.WebApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

RUN mkdir -p /app/data

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
ENV VERSIONS_JSON_PATH=/app/data/versions.json

EXPOSE 8080
ENTRYPOINT ["dotnet", "VaniFine.WebApi.dll"]