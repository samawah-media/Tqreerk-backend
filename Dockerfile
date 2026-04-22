FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Tqreerk-backend.sln", "./"]
COPY ["Tqreerk-backend/Tqreerk-backend.csproj", "Tqreerk-backend/"]
RUN dotnet restore "Tqreerk-backend/Tqreerk-backend.csproj"

COPY . .
RUN dotnet publish "Tqreerk-backend/Tqreerk-backend.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Tqreerk-backend.dll"]
