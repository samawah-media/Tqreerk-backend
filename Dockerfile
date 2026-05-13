FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Tqreerk-backend.sln", "./"]
COPY ["Tqreerk-backend/Tqreerk-backend.csproj", "Tqreerk-backend/"]
# Restore for linux-x64 explicitly. PublishReadyToRun (set in the csproj
# for Release builds) triggers an RID-specific publish; without --runtime
# here the restore writes a portable assets file and the publish step
# fails with NETSDK1047 "doesn't have a target for net8.0/linux-x64".
#
# -p:PublishReadyToRun=true is the second piece: `dotnet restore` defaults
# to Configuration=Debug, so the csproj's <PublishReadyToRun ... Release>
# condition does NOT fire here, and the R2R runtime package (carrying
# crossgen2) never gets pulled. Then the publish step would fail with
# NETSDK1094 "a valid runtime package was not found". Forcing the flag
# at restore time guarantees the package is on disk before publish runs.
RUN dotnet restore "Tqreerk-backend/Tqreerk-backend.csproj" \
    --runtime linux-x64 \
    -p:PublishReadyToRun=true

COPY . .
# --runtime linux-x64 matches the restore above. --no-self-contained keeps
# the publish framework-dependent — the final stage's `aspnet:8.0` base
# image already ships the runtime, so bundling it would only inflate the
# image by ~80 MB for no win.
RUN dotnet publish "Tqreerk-backend/Tqreerk-backend.csproj" \
    -c Release \
    --runtime linux-x64 \
    --no-self-contained \
    -o /app/publish \
    /p:UseAppHost=false \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Tqreerk-backend.dll"]
