# LumenMedia server — multi-stage build (скелет для будущих агентов).
# Драйверы GPU ставятся на ХОСТ, не в образ. Здесь только ffmpeg с HW-кодеками.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY . .
# Restore/publish the API project only — test projects are excluded via .dockerignore.
RUN dotnet restore src/LumenMedia.Api/LumenMedia.Api.csproj
RUN dotnet publish src/LumenMedia.Api/LumenMedia.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg + Intel VAAPI user-space driver (iHD, amd64 only). Host still needs /dev/dri + i915.
# non-free iHD package is required for Alder Lake-N / newer Intel GPUs; not available on arm64.
ARG TARGETARCH
RUN apt-get update \
    && ARCH="${TARGETARCH:-$(dpkg --print-architecture)}" \
    && apt-get install -y --no-install-recommends \
        ffmpeg curl vainfo \
        $( [ "$ARCH" = "amd64" ] && echo intel-media-va-driver-non-free || true ) \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Не root: ffmpeg парсит недоверенные медиафайлы — уязвимость демуксера не должна
# давать root в контейнере. Образ aspnet уже содержит пользователя `app` ($APP_UID).
# Для VAAPI пробрасывайте устройство и группу: --device /dev/dri --group-add video.
RUN mkdir -p /config /media /downloads \
    && chown -R $APP_UID:$APP_UID /config /media /downloads

VOLUME ["/config", "/media", "/downloads"]
EXPOSE 8096

ENV ASPNETCORE_URLS=http://+:8096 \
    LUMENMEDIA__Paths__Config=/config \
    LUMENMEDIA__Paths__Downloads=/downloads \
    LIBVA_DRIVER_NAME=iHD

USER $APP_UID

# HEALTHCHECK — эндпоинт /health появляется в фазе P0.
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -fsS http://localhost:8096/health || exit 1

ENTRYPOINT ["dotnet", "LumenMedia.Api.dll"]
