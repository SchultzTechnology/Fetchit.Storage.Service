FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
RUN mkdir -p /data/files

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Fetchit.Storage.Service/Fetchit.Storage.Service.csproj Fetchit.Storage.Service/
RUN dotnet restore Fetchit.Storage.Service/Fetchit.Storage.Service.csproj
COPY . .
WORKDIR /src/Fetchit.Storage.Service
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Fetchit.Storage.Service.dll"]
