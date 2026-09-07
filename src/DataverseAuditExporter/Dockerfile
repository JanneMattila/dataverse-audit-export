FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY DataverseAuditExporter.csproj ./
RUN dotnet restore DataverseAuditExporter.csproj
COPY *.cs ./
COPY appsettings.json ./
RUN dotnet publish DataverseAuditExporter.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "DataverseAuditExporter.dll"]