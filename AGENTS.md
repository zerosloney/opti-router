# AGENTS.md — OptiRouter 项目级说明

面向在本仓库工作的 agent：架构速览、生产部署方式、发版流程、已知坑。
用户级规则见 `C:\Users\Administrator\.zcode\AGENTS.md`，本文件只写项目特有事实。

## 架构速览

- .NET 单进程双职责：`/v1`、`/v1beta` OpenAI/Gemini 兼容代理路由 + Blazor Server 管理台（`/dashboard`、`/models` 等页面）。
- 存储：MariaDB（`OptiRouter:ConfigDbConnectionString`，见 publish 的 `appsettings.Production.json`）；未配置连接时回退 SQLite。
- 管理台鉴权：登录 Cookie（8h 滑动过期）或 `Authorization: Bearer <AdminApiKey>`；代理路径走 ProxyApiKey/租户 ClientKey。管理端路径前缀集中在 `Program.AdminPathPrefixes`（新增管理页面/接口必须同步）。
- 管理密钥在 `appsettings.json`（src 与 publish 两份保持一致）。

## 生产部署（本机）

- 生产实例 = Windows 服务 `OptiRouter`，由 nssm（`D:\nssm\nssm.exe`）托管：
  运行 `publish\OptiRouter.exe`，AppDirectory=publish，LocalSystem，开机自启。
- 地址 `http://localhost:5080`（urls 钉死在 appsettings.Production.json）；环境默认 Production，机器无 ASPNETCORE_* 环境变量覆盖。
- 日志：`publish\logs\service-*.log`（Serilog 按天滚动，正式日志）；
  `service-console.log` 为 nssm 重定向的 stdout/stderr（10MB 轮转，用于启动早期崩溃诊断）。

### 发版流程（必须按序；服务运行时锁 publish 文件）

```bash
D:/nssm/nssm.exe stop OptiRouter
dotnet publish src/OptiRouter/OptiRouter.csproj -c Release -o publish
D:/nssm/nssm.exe start OptiRouter
# 验证：curl http://localhost:5080/login → 200；tail publish/logs/service-*.log 看 "Application started"
```

- publish 会用 src 的 `appsettings*.json` 覆盖 publish 同名文件：发版前先 diff 两边，
  确认生产配置（MariaDB 连接串等）不会被旧版回写。

### 已知坑

- nssm 报错是 UTF-16 乱码："服务已存在"(1073) 常被误读成"拒绝访问"。排查先 `sc qc OptiRouter`：
  ImagePath 不带服务名参数的记录是残骸服务，`sc delete` 后重装。
- Production 开启 SingleInstanceGuard（Local\ 互斥锁）：publish 目录只允许一个实例。
  开发实例（`dotnet run`，端口 5157，Development，guard 关）不受影响，可与生产并存。
- Git Bash 会把 `/PID`、`/SC` 等参数转义成路径；停进程用 PowerShell `Stop-Process`，服务操作用 `sc.exe`/nssm。

## Blazor Server 会话保活（2026-08 修复，勿回退）

症状：管理面板常开超 8h 掉登录，断线后"重新连接"横幅永久卡死。
根因：Blazor Server 页面加载后浏览器不再发 HTTP 请求（全走 WebSocket），Cookie 的
8h 滑动过期无请求可续期；过期后重连 negotiate 被 302 到 /login，重连永久失败。

两道防线，移除任一都会复发：

1. `GET /api/dashboard/session/ping`（DashboardHandler，管理端中间件鉴权）+ blazor.js
   每 30 分钟浏览器侧 fetch 续期——必须由浏览器发起，Set-Cookie 才能落回浏览器，
   在 C# 电路里用 ApiService 调用是无效的。
2. blazor.js 轮询重连终态 class（`components-reconnect-failed`/`rejected`）自动整页刷新：
   Cookie 有效自愈，过期则被 302 回登录页；60s sessionStorage 防循环。

## 开发环境

- `dotnet run` → http://localhost:5157（launchSettings），Development，单实例守卫关闭。
- 测试：`dotnet test tests/OptiRouter.Tests`（集成测试自建 WebApplicationFactory 宿主，不读 launchSettings）。
