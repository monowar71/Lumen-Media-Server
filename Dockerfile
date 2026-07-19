# FreePlex server — multi-stage build (скелет для будущих агентов).
# Драйверы GPU ставятся на ХОСТ, не в образ. Здесь только ffmpeg с HW-кодеками.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY . .
# Restore/publish the API project only — test projects are excluded via .dockerignore.
RUN dotnet restore src/FreePlex.Api/FreePlex.Api.csproj
RUN dotnet publish src/FreePlex.Api/FreePlex.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg + Intel VAAPI user-space driver (iHD). Host still needs /dev/dri + i915.
# non-free iHD package is required for Alder Lake-N / newer Intel GPUs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg curl vainfo \
        intel-media-va-driver-non-free \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

VOLUME ["/config", "/media", "/downloads"]
EXPOSE 8096

ENV ASPNETCORE_URLS=http://+:8096 \
    FREEPLEX__Paths__Config=/config \
    FREEPLEX__Paths__Downloads=/downloads \
    LIBVA_DRIVER_NAME=iHD

# HEALTHCHECK — эндпоинт /health появляется в фазе P0.
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -fsS http://localhost:8096/health || exit 1

ENTRYPOINT ["dotnet", "FreePlex.Api.dll"]
