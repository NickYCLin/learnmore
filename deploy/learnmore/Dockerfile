# syntax=docker/dockerfile:1
# LearnMore 網站（ASP.NET Core net8.0）Linux 容器。build context 為 repo 根目錄：
#   docker compose -f deploy/learnmore/docker-compose.yml build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LearnMore/LearnMore.csproj LearnMore/
RUN dotnet restore LearnMore/LearnMore.csproj
COPY LearnMore/ LearnMore/
COPY docs/sql/ docs/sql/
RUN dotnet publish LearnMore/LearnMore.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# yt-dlp 的 YouTube JS challenge（EJS）需要 Node 22 以上；Node 20 會被判定為 unsupported
FROM node:22-bookworm-slim AS kuroshiro
WORKDIR /js
COPY LearnMore/wwwroot/js/package.json LearnMore/wwwroot/js/package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG YTDLP_VERSION=latest
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && if [ "$YTDLP_VERSION" = "latest" ]; then url=https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux; \
       else url=https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux; fi \
    && curl -fsSL "$url" -o /usr/local/bin/yt-dlp && chmod 0755 /usr/local/bin/yt-dlp \
    && printf '%s\n' '--js-runtimes node' > /etc/yt-dlp.conf
# Kuroshiro 轉換用 node（KuroshiroController 直接呼叫 `node`）
COPY --from=kuroshiro /usr/local/bin/node /usr/local/bin/node

WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=kuroshiro /js/node_modules ./wwwroot/js/node_modules

ENV ASPNETCORE_HTTP_PORTS=8080 \
    TZ=Asia/Taipei \
    LANG=zh_TW.UTF-8 \
    FFmpegPath=/usr/bin/ffmpeg \
    YtDlpPath=/usr/local/bin/yt-dlp \
    WhisperJsonPath=/var/lib/learnmore/data/whisper \
    AudioStemProcessing__WorkRoot=/var/lib/learnmore/data/audio-stems-work \
    PersistentSession__DataProtectionKeyPath=/var/lib/learnmore/dataprotection-keys \
    LEARNMORE_TRACE_DIR=/var/lib/learnmore/data/trace \
    Hosting__PathBase=/LearnMore \
    Hosting__UseForwardedHeaders=true

RUN mkdir -p /var/lib/learnmore/data/whisper /var/lib/learnmore/data/audio-stems-work /var/lib/learnmore/data/trace \
        /var/lib/learnmore/dataprotection-keys /var/lib/learnmore/cookies wwwroot/uploads wwwroot/audio-stems \
    && chown -R app:app /var/lib/learnmore wwwroot/uploads wwwroot/audio-stems
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "LearnMore.dll"]
