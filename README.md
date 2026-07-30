# MistChess

中国迷雾象棋，后端使用 .NET 10，前端使用 React 19 与 Vite，开发数据库使用 PostgreSQL 18。第二阶段包含 `600+5` 动态 Elo 匹配、连续棋钟、音效、游客私有历史、三视野回放和可撤销分享链接。

第三阶段的功能范围、数据口径、接口设计、安全要求和验收标准见 [`PHASE3_DEVELOPMENT.md`](PHASE3_DEVELOPMENT.md)。

## 准备环境

安装 `global.json` 指定的 .NET SDK、`.nvmrc` 指定的 Node.js，以及 PostgreSQL 18（默认 `localhost:5432`）。确保 `pwsh`、`dotnet`、`node`、`npm.cmd` 和 `psql` 均已加入 `PATH`。

## 本地配置与启动

仓库根目录的 `.env` 保存本机开发连接配置，并已被 Git 忽略。`Start-MistChess.ps1` 会以只解析键值对、不执行文件内容的方式加载它。新环境如果没有 `.env`，先复制 `.env.example`，再将连接字符串中的占位密码替换为本机专用密码。

运行仓库根目录的 `Start-MistChess.ps1`。脚本会检查开发工具、安装缺失的前端依赖，并应用待执行的 EF Core migrations。首次运行或 `.env` 密码与数据库角色不一致时，脚本会在当前窗口安全询问 PostgreSQL 管理员密码，然后创建或更新 `mistchess_app`、创建 `mistchess_dev` 并同步 `.env` 中的应用密码。输入的管理员密码不会显示。

随后脚本会在两个独立的 PowerShell 窗口中启动 API 与前端，确认数据库和两个服务就绪后打开站点。默认仅监听 `http://127.0.0.1:5173`。

管理员后台位于 `/admin`。本地开发先运行密码哈希工具；它只接受当前终端中的隐藏输入，不接受命令参数或管道输入，最终只输出 ASP.NET Core Identity 哈希：

```powershell
dotnet run --project tools/MistChess.AdminPasswordHash -c Release
```

复制输出的哈希，并把管理员用户名与哈希写入 API 项目的 .NET User Secrets。不要把管理员明文密码或生成后的哈希写入仓库：

```powershell
dotnet user-secrets set "Admin:Username" "<admin-username>" --project src/MistChess.Api
dotnet user-secrets set "Admin:PasswordHash" "<generated-password-hash>" --project src/MistChess.Api
```

`Start-MistChess.ps1` 以 Development 环境启动 API，会自动读取这组 User Secrets。缺少任一项时管理员登录保持禁用，普通游客功能不受影响。

如果 Windows 已将 `.ps1` 文件关联为 PowerShell 执行，可以直接双击脚本。Windows 默认文件关联可能会用编辑器打开 `.ps1`，此时请右键脚本并选择“使用 PowerShell 运行”，或在仓库根目录执行：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Start-MistChess.ps1
```

需要让同一局域网内的其他设备访问时，使用：

```powershell
.\Start-MistChess.ps1 -ListenOnLan
```

脚本每次启动都会从带 IPv4 默认网关的活动物理网卡中选择当前地址，并用该地址启动前端，不会保存或依赖固定局域网 IP。多网卡环境中如果自动选择结果不符合预期，可以用 `-WebHost` 显式传入本机当前的 IPv4 地址。

局域网模式只将 Vite 前端暴露在 TCP 5173；API 和数据库仍保持本机监听，浏览器请求由 Vite 同源代理转发。请仅在受信任网络中使用，并将 Windows 网络配置文件设为“专用”。其他设备仍无法连接时，允许 `node.exe` 通过 Windows Defender 防火墙的 TCP 5173 入站。

停止服务时，分别在 `MistChess API` 和 `MistChess Web` 窗口中按 `Ctrl+C`，然后关闭窗口。

忘记本机 `postgres` 管理员密码时，运行恢复脚本：

```powershell
.\scripts\database\Reset-LocalPostgresAdminPassword.ps1
```

脚本会请求 Windows 管理员权限并在本机窗口中读取两次新密码。它只临时放行 `127.0.0.1` 上的 `postgres` 角色，重置后立即恢复原始 `pg_hba.conf`，再同步 `.env` 中的应用密码和数据库 migrations。

集成测试维护连接通过进程环境变量提供，目标数据库必须为本机 `postgres` 维护库：

```powershell
$env:MISTCHESS_TEST_ADMIN_CONNECTION_STRING = "Host=localhost;Port=5432;Database=postgres;Username=mistchess_app;Password=<local-app-password>"
```

`.env.example` 仅列出变量形状；不要提交填入密码的 `.env` 文件。

## 数据库 migrations

```powershell
$env:ConnectionStrings__MistChess = "Host=localhost;Port=5432;Database=mistchess_dev;Username=mistchess_app;Password=<local-app-password>"
dotnet tool restore
dotnet tool run dotnet-ef migrations add <MigrationName> --project src/MistChess.Infrastructure/MistChess.Infrastructure.csproj --startup-project src/MistChess.Api/MistChess.Api.csproj
dotnet tool run dotnet-ef database update --project src/MistChess.Infrastructure/MistChess.Infrastructure.csproj --startup-project src/MistChess.Api/MistChess.Api.csproj
```

清理异常中止后遗留的测试数据库：

```powershell
./scripts/database/Remove-StaleTestDatabases.ps1
```

清理脚本连接且只连接本机 PostgreSQL 18 的 `postgres` 维护库，只枚举并删除符合 `mistchess_test_` 前缀和安全字符规则的数据库，删除前逐项确认，并强制保护 `postgres`、`template0`、`template1` 与 `mistchess_dev`。

## 测试

首次运行浏览器测试前安装两种浏览器：

```powershell
npx --prefix apps/web playwright install chromium firefox
```

完整浏览器套件包含管理员封禁与解封流程。运行前在当前 PowerShell 进程中设置专用测试管理员；用户名和哈希会传给 Playwright 启动的 API，明文密码只保留在 Playwright 进程中用于填写登录页，并会从 API 与 Vite 子进程环境中清除。E2E trace 已关闭，避免凭据进入失败产物：

```powershell
$env:MISTCHESS_E2E_ADMIN_USERNAME = "<e2e-admin-username>"
$env:MISTCHESS_E2E_ADMIN_PASSWORD = "<e2e-admin-plaintext-password>"
$env:MISTCHESS_E2E_ADMIN_PASSWORD_HASH = "<matching-generated-password-hash>"
```

这组凭据只用于本次测试，不要写入 `.env`、脚本或版本控制。测试已有部署时只需提供用户名和明文密码，并确保目标 API 已配置相同账号。

按上文设置测试维护连接字符串后，执行完整验证：

```powershell
dotnet test MistChess.sln
npm run lint --prefix apps/web
npm test --prefix apps/web
npm run e2e --prefix apps/web
```

Playwright 会先应用待执行的数据库 migrations，再自动启动开发 API 与 Vite，并在桌面 Chromium、Firefox 和 Pixel 5 移动 Chromium 三个项目中运行。它默认拒绝复用已有的 5052/5173 服务，避免误连旧代码或错误数据库；只有明确设置 `MISTCHESS_E2E_REUSE_SERVERS=1` 时才会复用。用已有同源部署做冒烟测试时，设置 `MISTCHESS_E2E_BASE_URL`；此时 Playwright 不会启动开发服务器。

## 生产发布

先在仓库根目录设置生产数据库连接，应用迁移，再构建组合发布产物。迁移必须在启动新版本进程前完成：

```powershell
$env:ConnectionStrings__MistChess = "<production-connection-string>"
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/MistChess.Infrastructure/MistChess.Infrastructure.csproj --startup-project src/MistChess.Api/MistChess.Api.csproj --configuration Release
npm run build --prefix apps/web
dotnet publish src/MistChess.Api/MistChess.Api.csproj -c Release -o artifacts/publish/api
```

后端发布目标会校验前端 `dist` 是否存在，并把静态资源组合进 `wwwroot`。生产环境使用进程级 `ConnectionStrings__MistChess`，不使用开发机 user-secrets。

管理员密码哈希可使用上文的 `MistChess.AdminPasswordHash` 工具生成。生产环境必须从服务管理器或密钥存储向进程注入 `Admin__Username` 与 `Admin__PasswordHash`；不要把管理员明文密码、哈希或生产连接字符串写入发布目录、脚本或版本控制。

### Kestrel 直接终止 TLS

在发布输出目录设置 HTTPS 监听、证书和唯一允许的前端 Origin，然后启动进程。`WebSockets__AllowedOrigins__0` 必须是浏览器访问站点的精确 `scheme://host[:port]`，不能包含路径：

```powershell
Set-Location artifacts/publish/api
$env:ConnectionStrings__MistChess = "<production-connection-string>"
$env:ASPNETCORE_URLS = "https://0.0.0.0:8443"
$env:Kestrel__Certificates__Default__Path = "C:\certs\mistchess.pfx"
$env:Kestrel__Certificates__Default__Password = "<certificate-password>"
$env:WebSockets__AllowedOrigins__0 = "https://chess.example.com:8443"
$env:Admin__Username = "<admin-username>"
$env:Admin__PasswordHash = "<generated-password-hash>"
dotnet MistChess.Api.dll
```

### 外部反向代理终止 TLS

同机代理只把 Kestrel 暴露到回环地址。代理必须转发 WebSocket，并成对发送 `X-Forwarded-For` 与 `X-Forwarded-Proto`：

```powershell
Set-Location artifacts/publish/api
$env:ConnectionStrings__MistChess = "<production-connection-string>"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5000"
$env:WebSockets__AllowedOrigins__0 = "https://chess.example.com"
$env:Admin__Username = "<admin-username>"
$env:Admin__PasswordHash = "<generated-password-hash>"
dotnet MistChess.Api.dll
```

代理位于另一台主机时，把 Kestrel 监听地址改为受防火墙保护的接口，并在启动前逐项配置代理的实际 IP：

```powershell
$env:ASPNETCORE_URLS = "http://0.0.0.0:5000"
$env:ReverseProxy__KnownProxies__0 = "<proxy-ip-address>"
```

生产入口必须使用 HTTPS。发布进程同源提供前端静态资源、`/api` 与 `/hubs`；存活和数据库就绪检查分别位于 `/health/live` 与 `/health/ready`。

## 生产监控

API 通过 `System.Diagnostics.Metrics` 发布名为 `MistChess.Api` 的 Meter。生产宿主可以使用 .NET 诊断工具或 OpenTelemetry Meter Provider 采集；应用指标只使用结果、人口档位、计时配置等低基数标签，不包含玩家 ID、棋局 ID、Cookie、令牌哈希或分享令牌。

核心仪表：

- `mistchess.matchmaking.tickets` 与 `mistchess.matchmaking.ticket.duration`：票据创建、成局、取消、过期及等待时长。
- `mistchess.matchmaking.scans`、`mistchess.matchmaking.eligible_population` 和 `mistchess.matchmaking.waiting.duration`：扫描时的有效人口、人口档位、是否不限分差和锚点等待时长。
- `mistchess.matchmaking.matches`、`mistchess.matchmaking.rating.difference` 和 `mistchess.matchmaking.match.duration`：各人口档位成局数、实际评分差和成局耗时。
- `mistchess.clock.timeouts`、`mistchess.clock.scan.delay` 和 `mistchess.clock.duplicate_completion_conflicts`：后台超时完成、扫描延迟和重复结束冲突。
- `mistchess.game.completions`、`mistchess.game.completion.duration`、`mistchess.rating.settlements` 和 `mistchess.rating.change`：终局原因、结算耗时、评分幂等命中和双方评分变化。
- `mistchess.history.list.duration`、`mistchess.replay.build.duration`、`mistchess.replay.frames`、`mistchess.replay.response.size` 和 `mistchess.replay.cache.validations`：历史查询、回放重建、压缩前后大小和 ETag 命中。
- `mistchess.share.operations`：分享创建、撤销、有效读取、无效读取和令牌限流。

建议仪表盘至少聚合以下发布指标：

1. `matchmaking.ticket.duration` 的成局 P50、P95，以及 `matched` 的 60 秒累计桶相对 `created` 计数得到的 60 秒内成局率。
2. `matchmaking.matches` 按 `population_band` 和 `unrestricted` 分组的成局数；同组 `rating.difference` 的平均值和 P95。
3. `clock.scan.delay` 的 P95 和 `duplicate_completion_conflicts` 增量。
4. `replay.build.duration`、`replay.response.size` 的 P95，`replay.cache.validations` 的命中率，以及 `share.operations` 中无效读取和限流占比。

默认匹配阈值已经由边界测试固定：有效人口 `2–4` 首轮不限分差；`5–9`、`10–19`、`20–49`、`50+` 的基础范围分别为 400、250、150、100；等待 15、30、45 秒增加 100、200、400，达到 60 秒不限分差。调整阈值前应对照上述分组指标，并重新运行 `Phase2PolicyTests` 和 PostgreSQL 匹配工作流测试。
