# MistChess 第三阶段开发规格

## 1. 目标

第三阶段交付以下能力：

1. 用清晰、厚重且容易区分的走棋音效与吃子音效替换当前通过变速复用单一提示音的实现。
2. 增加独立管理员登录与管理界面，支持封号、解封和查看用户状态。
3. 管理员可以查看每名用户的内部评分、胜率、胜负和及全部历史棋局。
4. 管理员可以查看当前在线用户。
5. 手机竖屏下将棋盘纵向尺寸增加 20%，并将棋子视觉尺寸增加 20%；桌面和手机横屏保持现状。

## 2. 现有基础与兼容约束

- 普通玩家使用 30 天有效期的游客会话，`GuestSessionEntity.Id` 是当前系统中的用户标识。第三阶段不引入普通用户注册、密码登录或账号合并。
- 评分按 `ruleVersion + timeControl` 存储于 `player_ratings`。当前主要评分档为当前规则版本的 `600+5` 快速匹配。
- 评分只允许管理员查看。不得向游客会话、普通对局、普通历史、回放分享或其他玩家接口增加 `rating`、`ratingChange` 等字段。
- 历史棋局、逐步局面和红方、黑方、全知三种回放视角已经存在，应复用现有 `HistoryService`、`HistoricalGameSummaryView` 和 `HistoricalReplayView` 的构建逻辑。
- 当前 `GameConnectionTracker` 只覆盖对局连接，不能作为全站在线状态来源。
- 现有音频服务已经具备用户开关、音量、本地持久化、浏览器自动播放解锁、同一权威版本去重和事件优先级，第三阶段保留这些行为。

## 3. 功能规格

### 3.1 走棋与吃子音效

#### 3.1.1 听感要求

- 走棋音效采用短促木质落子声，起音明确、无铃声感、无明显音高旋律，建议时长为 90 至 180 毫秒。
- 吃子音效采用比走棋更厚重的双层碰撞或木质重击声，低中频和瞬态强于走棋，建议时长为 180 至 350 毫秒。
- 在相同音量设置下，吃子必须可以立即与普通走棋区分；两者都必须明显强于当前“叮”声。
- 不复制第三方游戏音频。音频必须为项目原创、已购买授权或允许项目分发的素材，并在 `apps/web/public/audio/README.md` 记录来源和许可证。
- 文件必须去除首尾无效静音，峰值不得削波。建议走棋峰值约为 `-3 dBFS`，吃子峰值约为 `-2 dBFS`。

#### 3.1.2 资源与播放规则

新增以下本地资源，每种事件同时提供 Ogg Vorbis 和 MP3：

```text
apps/web/public/audio/move.ogg
apps/web/public/audio/move.mp3
apps/web/public/audio/capture.ogg
apps/web/public/audio/capture.mp3
```

`audioService.ts` 增加事件到资源的显式映射：

```text
move-self      -> move
move-opponent  -> move
capture        -> capture
```

其他事件可以继续使用现有通用提示音，本阶段不得通过降低或提高同一音频的播放速度来模拟走棋与吃子。走棋和吃子使用 `playbackRate = 1`，最终响度仍乘以用户的全局音量。

一次服务端权威版本变化只播放一个声音：

1. 终局声音优先于其他声音。
2. 吃子优先于普通走棋。
3. 普通走棋根据实际走子方发出 `move-self` 或 `move-opponent`，两者使用相同资源。
4. 初次加载、页面重连或重复收到同一版本不得重复播放走棋或吃子声音。
5. 浏览器尚未完成音频解锁时，继续沿用现有策略；非重要的历史走棋声音不得在用户稍后点击页面时补播。

#### 3.1.3 验收标准

- 完成普通走棋后播放一次走棋声，不播放当前通用“叮”声。
- 完成吃子后只播放一次吃子声，不同时播放走棋声。
- 自己走棋与对方走棋都能触发清晰走棋声。
- 重连并取得相同或更旧版本时不补播。
- 关闭音效或音量为零时不产生可听声音；重新开启后行为正常。
- Chromium、Firefox 和手机 Chromium 可以按浏览器支持情况自动选择 Ogg 或 MP3。

### 3.2 管理员身份与权限

#### 3.2.1 管理员账号

第三阶段提供一个由部署配置维护的管理员账号，不建立普通玩家账号体系。生产配置使用：

```text
Admin__Username
Admin__PasswordHash
```

`Admin__PasswordHash` 必须是由 ASP.NET Core `PasswordHasher` 生成的单向密码哈希。明文密码不得写入仓库、数据库迁移、日志、OpenAPI 或前端构建产物。

新增独立的管理员 Cookie 认证方案 `MistChessAdmin`：

- 生产 Cookie 名称：`__Host-MistChessAdmin`。
- `HttpOnly = true`、`Secure = true`、`SameSite = Strict`、`Path = /`。
- 绝对有效期 8 小时，不使用长期持久化或静默续期。
- 未登录访问管理员 API 返回 JSON `401`，无权限返回 JSON `403`，不得跳转到 HTML 登录页。
- 管理员登录接口按来源 IP 限制为 15 分钟最多 5 次失败；用户名或密码错误统一返回 `INVALID_ADMIN_CREDENTIALS`，不得泄露用户名是否存在。
- 所有管理员状态修改接口必须验证防跨站请求伪造令牌。

现有游客认证继续作为默认认证方案。管理员控制器显式指定管理员认证方案和 `admin` 授权策略，避免管理员 Cookie 被误识别为玩家身份，也避免游客 Cookie 访问管理员数据。

#### 3.2.2 管理员页面

新增独立路由：

```text
/admin/login
/admin/users
/admin/users/:playerId
/admin/games/:gameId
```

管理员路由不得包在 `SessionGate` 中，不得为管理员页面自动创建游客会话。建议增加 `AdminGate` 和独立 `AdminLayout`：

- `/admin/login`：用户名、密码、登录错误和提交中状态。
- `/admin/users`：用户表格、“全部用户”和“当前在线”两个筛选入口、名称或用户 ID 搜索、封禁状态筛选和游标分页。
- `/admin/users/:playerId`：用户基本信息、当前评分档、胜负和、胜率、封禁信息和全部历史棋局。
- `/admin/games/:gameId`：复用现有回放组件，默认允许管理员切换红方、黑方和全知视角。
- 页面提供显式退出登录操作；退出后清除管理员查询缓存并返回 `/admin/login`。

普通玩家页面不展示管理员入口。直接访问 `/admin` 时，根据管理员会话状态跳转到 `/admin/users` 或 `/admin/login`。

### 3.3 用户封号与解封

#### 3.3.1 数据模型

在 `guest_sessions` 增加：

```text
is_banned       boolean                  not null default false
banned_at       timestamp with time zone null
ban_reason      character varying(200)   null
banned_by       character varying(64)    null
last_seen_at    timestamp with time zone not null
```

约束：

- 未封禁时，`banned_at`、`ban_reason`、`banned_by` 必须同时为空。
- 已封禁时，`banned_at` 和 `banned_by` 必须有值；`ban_reason` 去除首尾空白后长度为 1 至 200。
- `last_seen_at` 初始值取迁移执行时已有会话的 `created_at`，新会话取创建时间。
- 为 `last_seen_at` 和 `is_banned` 建立适合管理员列表筛选的索引。

#### 3.3.2 封号行为

管理员封号必须在一个数据库事务中完成：

1. 锁定目标游客会话，重复封号返回当前状态，不重复执行终局或评分结算。
2. 设置封禁字段。
3. 取消目标用户所有 `Searching` 匹配票据。
4. 从尚未开始的私人房间移除该用户；如果该用户是房主，则按现有离房规则关闭房间。
5. 如果用户存在进行中棋局，以新增终局原因 `AdministrativeForfeit` 结束棋局，对手获胜。
6. 计分棋局按普通负局执行一次幂等评分结算；非计分棋局只结束棋局。
7. 清理 `game_players.is_active`，发送棋局结束通知，并向目标用户的实时连接发送 `AccountBanned` 通知。

封号对该游客会话立即生效：

- 认证处理程序继续解析用户 ID，并加入封禁声明。
- 默认玩家授权策略拒绝带封禁声明的主体。
- `/api/sessions/guest` 发现当前 Cookie 对应已封禁会话时返回 `403 PLAYER_BANNED`，不得为其静默创建新会话。
- 前端收到 `PLAYER_BANNED` 后显示封禁页和原因，不触发原有的 `401` 会话轮换流程。
- 已建立的 WebSocket 连接不能继续获得修改棋局的能力；所有 HTTP 命令仍在每次请求时重新鉴权。

解封只清除封禁字段，不恢复已取消的匹配票据、已退出的房间或已结束的棋局。封号针对现有游客身份；用户清除浏览器站点数据后会成为新的游客身份，该限制留待未来正式账号或设备风控体系解决。

### 3.4 评分、胜率与全部历史棋局

#### 3.4.1 数据口径

管理员用户列表中的主要评分取当前规则版本与 `600+5` 时间控制对应的 `PlayerRatingEntity`：

- 尚无评分记录时显示基础分 `1500`，计分局数为 `0`。
- 胜率计算为 `wins / gamesPlayed × 100%`，保留一位小数。
- `gamesPlayed = 0` 时胜率显示 `—`，API 返回 `winRate = null`。
- 同时显示 `wins`、`draws`、`losses`，便于核对胜率口径。
- 用户详情页可以列出该用户所有已有的 `ruleVersion + timeControl` 评分档，但默认突出当前快速匹配档。

历史棋局列表包含该用户参与的所有已结束棋局，包括计分与非计分棋局。历史列表按 `finishedAt DESC, gameId DESC` 排序，使用现有不透明游标分页，单页默认 20 条、最多 50 条。每条至少显示：

- 棋局 ID、结束时间、红黑双方名称。
- 用户执子方、胜负结果和终局原因。
- 规则版本、总计时、单步限时、是否计分。
- 总手数和查看回放入口。

管理员回放通过管理员专用接口读取，不要求管理员是棋局参与者，也不创建公开分享链接。接口可复用 `HistoryService.BuildReplay` 生成三种视角，禁止在查询中返回游客 Token 哈希或回放分享 Token 哈希。

#### 3.4.2 分页与查询

用户列表必须在数据库中分页和筛选，不得把全部用户加载到 API 或浏览器内存后再处理。支持：

```text
query       显示名子串或完整用户 ID
status      all | active | banned
online      all | online | offline
cursor      不透明游标
limit       1..50，默认 20
```

默认排序为 `lastSeenAt DESC, playerId DESC`。名称搜索使用 PostgreSQL 可索引或可控的大小写不敏感查询；用户数量增长后再根据实际查询计划决定是否增加 `pg_trgm`，第三阶段不预先引入扩展。

### 3.5 当前在线用户

在线状态以服务器时间和全站会话心跳为准：

- `SessionGate` 成功取得游客会话后立即发送一次心跳，此后每 30 秒发送一次。
- 页面从隐藏恢复为可见、浏览器恢复网络或 SignalR 重连成功时立即补发一次。
- 任意已认证玩家 API 请求也可以按最多每 30 秒一次的节流规则更新 `last_seen_at`，避免只依赖浏览器定时器。
- 满足 `expires_at > now`、`is_banned = false` 且 `last_seen_at >= now - 90 seconds` 时判定为在线。
- 关闭页面、断网或浏览器暂停后台任务后，用户最迟在 90 秒后转为离线。
- 管理员“当前在线”页每 15 秒重新查询；显示服务端返回的 `observedAt`，所有行使用同一个服务端判定时间，避免浏览器时钟偏差。

心跳接口不延长游客会话的 30 天过期时间，不返回评分或管理数据。服务端更新必须使用条件更新，只有 `last_seen_at` 早于当前时间 30 秒以上时才写数据库，降低持续写入压力。

### 3.6 手机竖屏界面

#### 3.6.1 生效范围

仅在以下媒体条件同时成立时应用：

```css
@media (max-width: 780px) and (orientation: portrait)
```

桌面、平板横屏和手机横屏继续使用当前 1:1 棋盘与棋子尺寸。

#### 3.6.2 棋盘尺寸

- 棋盘宽度继续使用容器可用宽度。
- 棋盘高度设为宽度的 `1.2` 倍，即 `aspect-ratio: 5 / 6`。
- SVG 在竖屏下纵向填满容器，棋盘网格、雾区、选择框、候选落点和交互覆盖层必须使用同一坐标变换，不能产生视觉位置与点击位置偏移。
- 现有 `viewBox="0 0 583 583"` 可以保留，并在竖屏下使用 `preserveAspectRatio="none"` 完成纵向变换；覆盖层继续按 `point / 583` 的百分比定位。

#### 3.6.3 棋子尺寸

纵向变换会自然把棋子高度放大到原来的 120%，还需把棋子内容在横向放大到原来的 120%，使最终屏幕中的棋子宽度和高度都为原来的 120%，保持圆形棋子为圆形、黑方多边形比例正常。实现时在棋子平移组内增加独立内容组，例如 `.board-piece__content`，竖屏下只对该内容组应用横向 `scaleX(1.2)`；不得覆盖外层负责棋盘坐标平移的 `transform`。

棋子文字、内圈和外轮廓与棋子一起缩放。点击热区保持至少 `44 × 44 CSS px`，并覆盖放大后的棋子；相邻热区不得导致不可预测的落子目标。选择圈和吃子候选圈应与放大后的棋子边缘匹配。

该规则同时作用于实时棋局和复盘棋盘，因为二者共用 `GameBoard`。页面不得产生横向滚动条，棋盘下方控制区保持可访问。

## 4. API 契约

### 4.1 管理员会话

```text
GET    /api/admin/antiforgery/token
POST   /api/admin/session
GET    /api/admin/session
DELETE /api/admin/session
```

登录请求：

```json
{
  "username": "admin",
  "password": "entered-password"
}
```

登录成功只返回管理员显示名和过期时间，不返回密码、哈希或认证票据内容。

### 4.2 用户管理

```text
GET    /api/admin/users
GET    /api/admin/users/{playerId}
POST   /api/admin/users/{playerId}/ban
DELETE /api/admin/users/{playerId}/ban
GET    /api/admin/users/{playerId}/games
GET    /api/admin/games/{gameId}/replay
```

封号请求：

```json
{
  "reason": "使用外挂"
}
```

用户列表响应项建议包含：

```json
{
  "playerId": "00000000-0000-0000-0000-000000000000",
  "displayName": "Guest 123456",
  "createdAt": "2026-07-27T00:00:00Z",
  "expiresAt": "2026-08-26T00:00:00Z",
  "lastSeenAt": "2026-07-27T00:00:00Z",
  "online": true,
  "banned": false,
  "banReason": null,
  "rating": 1500,
  "gamesPlayed": 0,
  "wins": 0,
  "draws": 0,
  "losses": 0,
  "winRate": null
}
```

分页响应必须包含 `items`、`nextCursor` 和 `observedAt`。封号、解封和回放接口对不存在的用户或棋局统一返回 `404`，防止从错误差异推断额外数据。

### 4.3 玩家会话心跳

```text
POST /api/sessions/heartbeat
```

成功返回 `204 No Content`。已封禁用户返回 `403 PLAYER_BANNED`，无效或过期会话返回 `401`。接口使用玩家维度限流和防跨站请求伪造保护。

## 5. 服务端实现分层

建议新增或扩展以下职责：

- `AdminAuthenticationHandler` 或 ASP.NET Core Cookie 方案：管理员登录票据、Cookie 和 JSON 认证失败行为。
- `AdminAuthorizationPolicy`：只允许管理员身份访问 `/api/admin/**`。
- `AdminUserService`：用户分页、评分汇总、详情、历史查询、封号和解封事务。
- `GuestPresenceService`：节流更新 `last_seen_at`，按统一 `observedAt` 计算在线状态。
- `GameCompletionService`：支持 `AdministrativeForfeit`，继续复用现有并发控制、终局通知和评分幂等结算。
- `HistoryService`：提取可复用的任意玩家历史查询与管理员回放入口；普通玩家入口仍强制参与者权限。

控制器只负责鉴权、参数绑定和调用应用服务。数据库查询不得在控制器中拼装。封号事务必须使用现有 PostgreSQL 行锁和幂等终局模式，避免管理员操作与走棋、超时或匹配成局竞争时重复结算。

## 6. 前端实现分层

建议新增：

```text
apps/web/src/features/admin/AdminGate.tsx
apps/web/src/routes/admin/AdminLoginPage.tsx
apps/web/src/routes/admin/AdminUsersPage.tsx
apps/web/src/routes/admin/AdminUserDetailPage.tsx
apps/web/src/routes/admin/AdminReplayPage.tsx
```

并扩展：

- `App.tsx`：增加独立管理员路由树。
- `api/client.ts`、`api/queryKeys.ts`：增加管理员会话、用户分页、封号、解封、历史和回放请求；管理员退出时清理 `admin` 查询根。
- `SessionGate.tsx`：全站心跳、可见性恢复心跳和 `PLAYER_BANNED` 专用页面。
- `audioService.ts`：按事件选择独立资源。
- `GameBoard.tsx` 与 `index.css`：竖屏几何和棋子内容组缩放。

管理员页面必须支持加载、空结果、接口错误、会话过期、操作确认和操作中禁用状态。封号确认框显示用户名称，并要求输入 1 至 200 字原因；解封需要二次确认。成功后使用户详情、全部用户和在线用户查询同时失效并刷新。

## 7. 安全与隐私

- 管理员 API 必须经过服务端授权；隐藏前端链接不构成权限控制。
- 普通玩家无法通过修改 URL、请求参数或 Cookie 读取任意用户评分和历史。
- 管理员接口不得返回 `token_hash`、密码哈希、连接字符串、回放分享 Token 哈希或完整认证声明。
- 管理员登录、封号、解封记录结构化日志，包含管理员名、目标玩家 ID、动作和时间，不记录密码与游客 Token。
- `ban_reason` 会显示给被封用户，管理员界面必须明确提示不得写入密码、联系方式或其他无关敏感信息。
- 用户列表查询、历史查询和管理员登录分别设置限流策略，不能复用宽松的普通资源策略。
- 生产环境缺少管理员用户名或密码哈希时，管理员登录功能保持关闭并记录明确启动警告；不得使用默认密码。

## 8. 数据库迁移

创建一个第三阶段迁移，至少包含：

1. `guest_sessions` 的封禁与在线字段、检查约束和索引。
2. `games.result_reason` 对 `AdministrativeForfeit` 的兼容；当前字段为字符串，无需新枚举列，但领域、契约和前端文案必须同步增加枚举值。
3. 模型快照更新。

迁移必须可在已有生产数据上执行。已有用户的 `is_banned` 为 `false`，`last_seen_at` 回填为 `created_at`；不得重建或清空对局、评分和历史表。

## 9. 验证方案

### 9.1 服务端

- 管理员正确登录、错误凭据、登录限流、过期 Cookie、退出和防跨站请求伪造测试。
- 游客访问所有管理员接口均为 `401` 或 `403`，且响应不含评分和历史数据。
- 用户列表的搜索、状态、在线筛选、游标稳定性、零局胜率和评分档选择测试。
- 任意用户历史只返回该用户参与的全部已结束棋局，跨页无重复和遗漏。
- 管理员可以读取任意已结束棋局的三视角回放；普通玩家权限保持不变。
- 心跳 30 秒写入节流、90 秒在线边界、过期和封禁排除测试，时间使用可注入 `TimeProvider`。
- 封号与走棋、超时并发时只生成一次终局和一次评分结算。
- 封禁进行中计分棋局时，对手获胜、封禁方记负；私人棋局不产生评分。
- 重复封号幂等；解封不恢复已结束或已取消资源。
- OpenAPI 生成后，普通会话契约仍不含任何评分字段。

### 9.2 前端

- `AudioService` 测试断言走棋与吃子加载不同文件，Ogg/MP3 回退、音量、禁用、自动播放解锁和同版本去重保持正常。
- `GamePage` 测试断言普通走棋只触发 `move`，吃子只触发 `capture`，终局仍覆盖走棋事件。
- 管理员登录、会话过期、用户分页、在线筛选、封号、解封和历史空状态组件测试。
- 被封用户收到 `PLAYER_BANNED` 后显示封禁页，不创建新游客会话。
- `GameBoard` 在竖屏媒体条件下边界框高宽比为 `1.2`，棋子屏幕宽高均为桌面基准的 `1.2` 倍，交互热区中心与棋子中心重合。
- Pixel 5 竖屏浏览器检查实时棋局和回放无横向滚动；Pixel 5 横屏和桌面尺寸保持原样。

### 9.3 端到端场景

1. 玩家 A 打开首页并保持在线，管理员登录后在“当前在线”中看到玩家 A。
2. 玩家 A 进入快速匹配并开局，管理员打开其详情，看到内部评分和历史列表。
3. 管理员填写原因并封禁玩家 A，当前棋局以封禁判负结束，对手收到终局通知。
4. 玩家 A 的下一次命令返回 `PLAYER_BANNED`，页面显示封禁原因且不会生成新游客身份。
5. 管理员解封玩家 A，玩家 A 刷新后可重新进入，但原棋局与原评分结算不回滚。
6. 在手机竖屏完成一次普通走棋和一次吃子，确认棋盘纵向增加 20%、棋子保持比例并增加 20%，两个声音清晰可分辨。

## 10. 完成定义

以下条件全部满足后，第三阶段才算完成：

- 走棋与吃子使用两个独立、授权清楚且听感显著不同的音频资源。
- 管理员认证、授权、登录限流和防跨站请求伪造保护在服务端生效。
- 管理员可以分页查看全部用户、当前在线用户、内部评分、胜率和全部历史棋局，并可打开三视角回放。
- 封号和解封端到端可用，封号可以安全处理匹配、房间、进行中棋局和评分结算。
- 普通用户仍无法看到评分或访问他人的私有历史。
- 手机竖屏棋盘高度和棋子尺寸达到 120%，点击位置准确；桌面与横屏没有尺寸回归。
- 数据库迁移可以保留现有生产数据执行。
- 相关服务端、前端和端到端验证全部通过，并完成真实浏览器听感与竖屏交互检查。
