# Build assets
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /App
COPY . ./
RUN dotnet fsi ./build.fsx -- -p publish --docker

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /App
COPY --from=build-env /App/publish/docker .
ENTRYPOINT ["dotnet", "Brainloop.dll"]
