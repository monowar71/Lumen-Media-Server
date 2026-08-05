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
# yt-dlp YouTube extraction needs a supported JS runtime (Deno >= 2.3; apt nodejs is too old).
ARG DENO_VERSION=2.4.3
# Bundled TorrServer (YouROK) — lazy-started on torrent playback; GPL-3.0.
ARG TORRSERVER_VERSION=MatriX.142.2
RUN apt-get update \
    && ARCH="${TARGETARCH:-$(dpkg --print-architecture)}" \
    && apt-get install -y --no-install-recommends \
        ffmpeg curl unzip vainfo ca-certificates python3 \
        $( [ "$ARCH" = "amd64" ] && echo intel-media-va-driver-non-free || true ) \
    && curl -fsSL -o /usr/local/bin/yt-dlp \
        https://github.com/yt-dlp/yt-dlp/releases/download/2026.07.04/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && case "$ARCH" in \
         amd64) DENO_ARCH=x86_64; TS_ARCH=amd64 ;; \
         arm64) DENO_ARCH=aarch64; TS_ARCH=arm64 ;; \
         *) echo "unsupported arch: $ARCH" >&2; exit 1 ;; \
       esac \
    && curl -fsSL -o /tmp/deno.zip \
        "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-${DENO_ARCH}-unknown-linux-gnu.zip" \
    && unzip -o /tmp/deno.zip -d /usr/local/bin \
    && chmod a+rx /usr/local/bin/deno \
    && rm -f /tmp/deno.zip \
    && curl -fsSL -o /usr/local/bin/torrserver \
        "https://github.com/YouROK/TorrServer/releases/download/${TORRSERVER_VERSION}/TorrServer-linux-${TS_ARCH}" \
    && chmod a+rx /usr/local/bin/torrserver \
    && yt-dlp --version \
    && deno --version \
    && /usr/local/bin/torrserver --help >/dev/null 2>&1 || true \
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
