# Build from the repository root:  docker build -f NugetProxy/Dockerfile -t nugetproxy .
#
# A chiseled base: no shell and no package manager, and it runs as the non-root "app" user.
# The plain chiseled image has no ICU, so the app runs with invariant globalization (see the csproj);
# nothing here depends on culture-specific behaviour. If that ever changes, switch to
# 10.0-resolute-chiseled-extra, which adds ICU and tzdata.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute-chiseled AS base
WORKDIR /app
ENV CacheRoot=/cache
EXPOSE 5182

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["NugetProxy/NugetProxy.csproj", "NugetProxy/"]
RUN dotnet restore "NugetProxy/NugetProxy.csproj"
COPY NugetProxy/ NugetProxy/
RUN dotnet publish "NugetProxy/NugetProxy.csproj" -c $BUILD_CONFIGURATION --no-restore -o /app/publish /p:UseAppHost=false
# The chiseled image has no shell to create the cache directory in, so it is made here and copied in.
RUN mkdir /cache

FROM base AS final
# Package files are kept here; mount a volume so they survive a restart. A new volume takes its owner
# from the image's directory, so it must belong to the app user for the cache to be writable.
COPY --from=build --chown=$APP_UID:$APP_UID /cache /cache
VOLUME /cache
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "NugetProxy.dll"]
