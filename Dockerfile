FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish GB32960.Server/GB32960.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 32960
VOLUME ["/app/Logs", "/app/RawData"]
ENTRYPOINT ["dotnet", "GB32960.Server.dll"]
