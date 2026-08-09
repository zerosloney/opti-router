# syntax=docker/dockerfile:1
# 多阶段构建：sdk 阶段编译发布，aspnet 阶段仅承载运行时产物。
# 目标框架 net8.0（见 src/OptiRouter/OptiRouter.csproj）。

# ---- 构建阶段 ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先拷 csproj 单独 restore，利用 Docker 层缓存（依赖未变时跳过还原）。
COPY OptiRouter.sln ./
COPY src/OptiRouter/OptiRouter.csproj ./src/OptiRouter/
COPY tests/OptiRouter.Tests/OptiRouter.Tests.csproj ./tests/OptiRouter.Tests/
RUN dotnet restore OptiRouter.sln

# 拷源码并发布 Release。--no-restore 跳过（已还原）。
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet publish src/OptiRouter/OptiRouter.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

# ---- 运行阶段 ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# 非 root 运行（安全最佳实践）。安装 curl 供 HEALTHCHECK 探测 /health。
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && adduser --disabled-password --gecos "" --uid 1000 app \
    && mkdir -p /app/data \
    && chown -R app:app /app

# 仅拷发布产物（无 SDK、无源码、无 obj/bin），镜像体积最小化。
COPY --from=build /app/publish ./

USER app

# Kestrel 监听 5000。容器内 HTTP，TLS 由外部反代终结（见 README 部署章节）。
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    # SQLite 持久化账本路径（与 appsettings 默认 data/ 相对路径一致）。
    OptiRouter__Budget__StorePath=data/optirouter-budget.db

EXPOSE 5000

# 健康检查：/health 无需 API Key，5 秒探测。
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:5000/health || exit 1

# 数据卷：SQLite 账本持久化。挂载宿主目录可跨容器重建保留成本数据。
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "OptiRouter.dll"]
