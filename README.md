# MistChess

中国迷雾象棋首版，后端使用 .NET 10，前端使用 React 19 与 Vite，开发数据库使用 PostgreSQL 18。

## 准备环境

安装 `global.json` 指定的 .NET SDK、`.nvmrc` 指定的 Node.js，以及 PostgreSQL 18（默认 `localhost:5432`）。确保 `pwsh`、`dotnet`、`node`、`npm.cmd` 和 `psql` 均已加入 `PATH`。

## 本地配置与启动

仓库根目录的 `.env` 保存本机开发连接配置，并已被 Git 忽略。`Start-MistChess.ps1` 会以只解析键值对、不执行文件内容的方式加载它。新环境如果没有 `.env`，先复制 `.env.example`，再将连接字符串中的占位密码替换为本机专用密码。

运行仓库根目录的 `Start-MistChess.ps1`。脚本会检查开发工具、安装缺失的前端依赖，并应用待执行的 EF Core migrations。首次运行或 `.env` 密码与数据库角色不一致时，脚本会在当前窗口安全询问 PostgreSQL 管理员密码，然后创建或更新 `mistchess_app`、创建 `mistchess_dev` 并同步 `.env` 中的应用密码。输入的管理员密码不会显示。

随后脚本会在两个独立的 PowerShell 窗口中启动 API 与前端，确认数据库和两个服务就绪后打开 `http://127.0.0.1:5173`。

如果 Windows 已将 `.ps1` 文件关联为 PowerShell 执行，可以直接双击脚本。Windows 默认文件关联可能会用编辑器打开 `.ps1`，此时请右键脚本并选择“使用 PowerShell 运行”，或在仓库根目录执行：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Start-MistChess.ps1
```

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

按上文设置测试维护连接字符串后，执行完整验证：

```powershell
dotnet test MistChess.sln
npm run lint --prefix apps/web
npm test --prefix apps/web
npm run e2e --prefix apps/web
```

Playwright 会自动启动开发 API 与 Vite，并在桌面 Chromium、Firefox 和 Pixel 5 移动 Chromium 三个项目中运行。用已有同源部署做冒烟测试时，设置 `MISTCHESS_E2E_BASE_URL`；此时 Playwright 不会启动开发服务器。

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

### Kestrel 直接终止 TLS

在发布输出目录设置 HTTPS 监听、证书和唯一允许的前端 Origin，然后启动进程。`WebSockets__AllowedOrigins__0` 必须是浏览器访问站点的精确 `scheme://host[:port]`，不能包含路径：

```powershell
Set-Location artifacts/publish/api
$env:ConnectionStrings__MistChess = "<production-connection-string>"
$env:ASPNETCORE_URLS = "https://0.0.0.0:8443"
$env:Kestrel__Certificates__Default__Path = "C:\certs\mistchess.pfx"
$env:Kestrel__Certificates__Default__Password = "<certificate-password>"
$env:WebSockets__AllowedOrigins__0 = "https://chess.example.com:8443"
dotnet MistChess.Api.dll
```

### 外部反向代理终止 TLS

同机代理只把 Kestrel 暴露到回环地址。代理必须转发 WebSocket，并成对发送 `X-Forwarded-For` 与 `X-Forwarded-Proto`：

```powershell
Set-Location artifacts/publish/api
$env:ConnectionStrings__MistChess = "<production-connection-string>"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5000"
$env:WebSockets__AllowedOrigins__0 = "https://chess.example.com"
dotnet MistChess.Api.dll
```

代理位于另一台主机时，把 Kestrel 监听地址改为受防火墙保护的接口，并在启动前逐项配置代理的实际 IP：

```powershell
$env:ASPNETCORE_URLS = "http://0.0.0.0:5000"
$env:ReverseProxy__KnownProxies__0 = "<proxy-ip-address>"
```

生产入口必须使用 HTTPS。发布进程同源提供前端静态资源、`/api` 与 `/hubs`；存活和数据库就绪检查分别位于 `/health/live` 与 `/health/ready`。
