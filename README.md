# LumenMedia Server

.NET 10 backend for LumenMedia. Layered (hexagonal) architecture, SQLite-embedded, contract-first (OpenAPI).

> Вся разработка ведётся **только через Docker** — на хосте .NET 10 не требуется.
> Базовый образ: `mcr.microsoft.com/dotnet/sdk:10.0`.

## Структура решения

```
server/
├─ LumenMedia.slnx                 # решение (новый XML-формат)
├─ global.json                   # пин SDK 10.0.x
├─ Directory.Build.props         # общие свойства (net10.0, nullable, warnings-as-errors)
├─ Dockerfile                    # multi-stage build (sdk → aspnet + ffmpeg)
├─ openapi.json                  # экспортированный контракт (коммитится)
├─ src/
│  ├─ LumenMedia.Domain            # сущности, инварианты. Без внешних зависимостей.
│  ├─ LumenMedia.Application       # порты (интерфейсы), use-case сервисы, валидация, DTO
│  ├─ LumenMedia.Infrastructure    # EF Core + SQLite, репозитории, JWT, ffprobe/ffmpeg, сканер, воркеры
│  └─ LumenMedia.Api               # ASP.NET Core: контроллеры, JWT, ProblemDetails, OpenAPI, SignalR
└─ tests/
   ├─ LumenMedia.Domain.Tests
   ├─ LumenMedia.Application.Tests # NameParser, PlaybackDecider, use-cases, архитектурные тесты
   └─ LumenMedia.Api.IntegrationTests # WebApplicationFactory + реальный SQLite
```

Направление зависимостей: `Api → Application → Domain`, `Infrastructure → Application/Domain`. Domain ни от чего не зависит (проверяется `ArchitectureTests`).

## Команды (Docker)

Общий шаблон (монтируем репозиторий, переиспользуем кэш NuGet):

```bash
docker run --rm -v "$PWD/..":/src -w /src/server \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 <команда>
```

### Сборка

```bash
docker run --rm -v "$PWD/..":/src -w /src/server \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet build LumenMedia.slnx
```

### Тесты

```bash
docker run --rm -v "$PWD/..":/src -w /src/server \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test LumenMedia.slnx
```

### Миграции EF Core

```bash
docker run --rm -v "$PWD/..":/src -w /src/server \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c \
  "dotnet tool install --global dotnet-ef --version 10.0.* ; export PATH=\$PATH:/root/.dotnet/tools ; \
   dotnet ef migrations add <Name> -p src/LumenMedia.Infrastructure -s src/LumenMedia.Infrastructure -o Persistence/Migrations"
```

Миграции применяются автоматически при старте (`Database.Migrate()`), отдельный шаг деплоя не нужен.

### Запуск и экспорт OpenAPI

```bash
docker run --rm -p 5080:5080 -v "$PWD/..":/src -w /src/server \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project src/LumenMedia.Api --urls http://0.0.0.0:5080
# затем:  GET /health, GET /openapi/v1.json
```

`openapi.json` в корне `server/` — экспорт эндпоинта `/openapi/v1.json`. Пересоздать после изменений API.

### Продакшн-образ

```bash
docker build -t lumenmedia-server .
docker run --rm -p 8096:8096 -v lumenmedia-config:/config -v /path/to/media:/media lumenmedia-server
```

## Конфигурация

| Ключ | Env | По умолчанию |
|---|---|---|
| `LumenMedia:Database:ConnectionString` | `LUMENMEDIA__Database__ConnectionString` | `Data Source=/config/lumenmedia.db` |
| `LumenMedia:Paths:Config` | `LUMENMEDIA__Paths__Config` | `/config` |
| `LumenMedia:Paths:Transcodes` | `LUMENMEDIA__Paths__Transcodes` | `/config/transcodes` |
| `LumenMedia:Transcoding:*` | `LUMENMEDIA__Transcoding__*` | см. `appsettings.json` |
| `Jwt:Secret` | `JWT__SECRET` | **обязателен в Production** (min 32 байта; при отсутствии сервер не стартует). Вне Production генерируется эфемерный ключ |
| `Cors:AllowedOrigins` | `CORS__ALLOWEDORIGINS__0` … | пусто = разрешён любой origin (LAN-режим); для интернет-доступа задайте список origin'ов |

> Секреты (`Jwt:Secret`, ключи TMDB/TVDB) — только через env/user-secrets, никогда в репозитории или логах.
>
> Логин/refresh/setup ограничены rate limiter'ом: 10 запросов в минуту с одного IP (429 при превышении).
>
> Продакшн-образ работает от непривилегированного пользователя `app`. Для HW-транскодирования пробросьте GPU: `--device /dev/dri --group-add video` (при необходимости и `--group-add render`). Bind-mount'ы `/config`, `/media`, `/downloads` должны быть доступны на запись UID 1654 (`app`).

## Первый запуск

1. `GET /api/v1/server/info` → `setupCompleted: false`.
2. `POST /api/v1/setup` `{ "username", "password" }` → создаёт первого администратора.
3. `POST /api/v1/auth/login` → `accessToken` + `refreshToken`.
4. Далее — `Authorization: Bearer <accessToken>`.
