import {
  devices,
  expect,
  test,
  type APIResponse,
  type Browser,
  type BrowserContext,
  type BrowserContextOptions,
  type Page,
  type TestInfo,
  type WebSocket,
} from '../apps/web/node_modules/@playwright/test/index.js'
import type {
  GameView,
  GuestSession,
  MoveRequest,
  Position,
  Side,
} from '../apps/web/src/api/types.js'

interface PlayerClient {
  context: BrowserContext
  page: Page
  session: GuestSession
}

interface HubInvocation {
  type?: number
  target?: string
  arguments?: unknown[]
}

interface HubCapture {
  invocations: HubInvocation[]
  rawFrames: string[]
}

const baseUrl = process.env.MISTCHESS_E2E_BASE_URL ?? 'http://127.0.0.1:5173'
const gamePathPattern = /\/game\/([^/?#]+)$/

function browserContextOptions(testInfo: TestInfo, playerIndex: number): BrowserContextOptions {
  const device = testInfo.project.name === 'mobile-chromium'
    ? devices['Pixel 5']
    : testInfo.project.name === 'firefox'
      ? devices['Desktop Firefox']
      : devices['Desktop Chrome']
  const projectAddress = testInfo.project.name === 'firefox'
    ? 20
    : testInfo.project.name === 'mobile-chromium'
      ? 30
      : 10

  return {
    ...device,
    baseURL: baseUrl,
    ignoreHTTPSErrors: true,
    extraHTTPHeaders: {
      'X-Forwarded-For': `198.51.100.${projectAddress + playerIndex}`,
      'X-Forwarded-Proto': new URL(baseUrl).protocol.slice(0, -1),
    },
  }
}

async function installAudioProbe(context: BrowserContext): Promise<void> {
  await context.addInitScript(() => {
    const sounds: string[] = []
    ;(window as Window & { __mistChessSounds?: string[] }).__mistChessSounds = sounds
    Object.defineProperty(HTMLMediaElement.prototype, 'play', {
      configurable: true,
      value(this: HTMLMediaElement) {
        const soundEvent = this.dataset.soundEvent
        if (soundEvent && this.volume > 0) sounds.push(soundEvent)
        return Promise.resolve()
      },
    })
    Object.defineProperty(HTMLMediaElement.prototype, 'pause', {
      configurable: true,
      value() {},
    })
  })
}

async function playedSounds(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    (window as Window & { __mistChessSounds?: string[] }).__mistChessSounds ?? [])
}

function waitForResponse(page: Page, path: string, method = 'POST'): Promise<APIResponse> {
  return page.waitForResponse((response) =>
    new URL(response.url()).pathname === path && response.request().method() === method)
}

function waitForHubSocket(page: Page, path: '/hubs/lobby' | '/hubs/game'): Promise<WebSocket> {
  return page.waitForEvent('websocket', {
    predicate: (socket) => new URL(socket.url()).pathname === path,
  })
}

async function openPlayerAtHome(context: BrowserContext): Promise<PlayerClient> {
  const page = await context.newPage()
  const sessionResponsePromise = waitForResponse(page, '/api/sessions/guest')
  await page.goto('/')
  const sessionResponse = await sessionResponsePromise
  expect(sessionResponse.ok()).toBeTruthy()
  const session = await sessionResponse.json() as GuestSession
  await expect(page.getByRole('button', { name: '寻找对手' })).toBeVisible()
  await expect(page.getByRole('button', { name: '创建房间' })).toBeVisible()
  return { context, page, session }
}

async function openTwoPlayers(
  browser: Browser,
  testInfo: TestInfo,
): Promise<[PlayerClient, PlayerClient]> {
  const [contextA, contextB] = await Promise.all([
    browser.newContext(browserContextOptions(testInfo, 0)),
    browser.newContext(browserContextOptions(testInfo, 1)),
  ])
  await Promise.all([
    installAudioProbe(contextA),
    installAudioProbe(contextB),
  ])
  return Promise.all([
    openPlayerAtHome(contextA),
    openPlayerAtHome(contextB),
  ])
}

async function restoreSessionByReload(player: PlayerClient): Promise<void> {
  const sessionResponsePromise = waitForResponse(player.page, '/api/sessions/guest')
  await player.page.reload()
  const sessionResponse = await sessionResponsePromise
  expect(sessionResponse.ok()).toBeTruthy()
  const restored = await sessionResponse.json() as GuestSession
  expect(restored.playerId).toBe(player.session.playerId)
  expect(restored.displayName).toBe(player.session.displayName)
}

function captureHub(socket: WebSocket): HubCapture {
  const capture: HubCapture = { invocations: [], rawFrames: [] }
  let pending = ''

  socket.on('framereceived', (event) => {
    const frame = event.payload.toString()
    capture.rawFrames.push(frame)
    pending += frame
    const messages = pending.split('\u001e')
    pending = messages.pop() ?? ''

    for (const message of messages) {
      if (!message) continue
      try {
        capture.invocations.push(JSON.parse(message) as HubInvocation)
      } catch {
        // Non-JSON frames are retained in rawFrames for protocol-boundary assertions.
      }
    }
  })

  return capture
}

function hubArguments<T>(capture: HubCapture, targets: readonly string[]): T[] {
  const values: T[] = []
  for (const invocation of capture.invocations) {
    if (!invocation.target || !targets.includes(invocation.target)) continue
    const value = invocation.arguments?.[0]
    if (value !== undefined) values.push(value as T)
  }
  return values
}

function lobbyGameId(capture: HubCapture): string | undefined {
  const notifications = hubArguments<{ gameId?: string; status?: string }>(
    capture,
    ['MatchTicketUpdated', 'MatchFound'],
  )
  return notifications.findLast((notification) =>
    notification.gameId && (!notification.status || notification.status === 'matched'))?.gameId
}

function gameViews(capture: HubCapture): GameView[] {
  return hubArguments<GameView>(capture, ['GameViewUpdated', 'GameEnded'])
}

function latestGameView(capture: HubCapture): GameView | undefined {
  const views = gameViews(capture)
  return views.reduce<GameView | undefined>(
    (latest, view) => !latest || view.version >= latest.version ? view : latest,
    undefined,
  )
}

function assertPlayerScopedProtocol(
  captures: readonly HubCapture[],
  perspective: Side,
  gameId: string,
): void {
  const views = captures.flatMap(gameViews)
  expect(views.length).toBeGreaterThan(0)
  for (const view of views) {
    expect(view.gameId).toBe(gameId)
    expect(view.perspective).toBe(perspective)
  }

  const protocol = captures.flatMap((capture) => capture.rawFrames).join('\n')
  expect(protocol).not.toContain('checkedSide')
  expect(protocol).not.toContain('isInCheck')
  expect(protocol).not.toContain('generalThreatened')
}

function gameIdFromPage(page: Page): string {
  const match = new URL(page.url()).pathname.match(gamePathPattern)
  if (!match) throw new Error(`Expected a game route, received ${page.url()}`)
  return decodeURIComponent(match[1])
}

async function expectGameHubConnected(page: Page): Promise<void> {
  const connection = page.locator('.game-connection')
  await expect(connection).toHaveAttribute('data-state', 'connected')
  await expect(connection).toContainText('实时同步')
}

async function domGameVersion(page: Page): Promise<number> {
  const text = await page.getByText(/^局面版本 \d+$/).textContent()
  const match = text?.match(/局面版本 (\d+)/)
  if (!match) throw new Error(`Could not read game version from ${text ?? 'empty text'}`)
  return Number(match[1])
}

async function domPerspective(page: Page): Promise<Side> {
  const label = await page.getByTestId('game-board').locator('svg.game-board__svg').getAttribute('aria-label')
  if (label?.startsWith('红方视角')) return 'red'
  if (label?.startsWith('黑方视角')) return 'black'
  throw new Error(`Could not read board perspective from ${label ?? 'empty label'}`)
}

async function domFogSquares(page: Page): Promise<string[]> {
  return page
    .getByTestId('game-board')
    .locator('[data-testid^="fog-"]')
    .evaluateAll((elements) => elements
      .map((element) => element.getAttribute('data-testid'))
      .filter((value): value is string => value !== null)
      .sort())
}

function fogSquaresFromView(view: GameView): string[] {
  const visible = new Set(view.visibleSquares.map((position) => `${position.file}:${position.rank}`))
  const fog: string[] = []
  for (let rank = 0; rank < 10; rank += 1) {
    for (let file = 0; file < 9; file += 1) {
      const key = `${file}:${rank}`
      if (!visible.has(key)) fog.push(`fog-${key}`)
    }
  }
  return fog.sort()
}

function positionFromKey(key: string): Position {
  const [file, rank] = key.split(':').map(Number)
  return { file, rank }
}

async function submitFirstBoardMove(page: Page, gameId: string): Promise<{
  from: Position
  to: Position
}> {
  const board = page.getByTestId('game-board')
  const sourceKeys = await board
    .locator('button[data-position]:enabled:not([data-candidate])')
    .evaluateAll((buttons) => buttons
      .map((button) => button.getAttribute('data-position'))
      .filter((value): value is string => value !== null))
  expect(sourceKeys.length).toBeGreaterThan(0)

  for (const sourceKey of sourceKeys) {
    await board.locator(`button[data-position="${sourceKey}"]:not([data-candidate])`).click()
    const destination = board.locator('button[data-position][data-candidate="true"]:enabled').first()
    if (await destination.count() === 0) continue

    const destinationKey = await destination.getAttribute('data-position')
    if (!destinationKey) throw new Error('Candidate destination did not expose data-position')
    const moveResponsePromise = waitForResponse(page, `/api/games/${gameId}/moves`)
    await destination.click()
    const moveResponse = await moveResponsePromise
    expect(moveResponse.ok()).toBeTruthy()

    const submitted = moveResponse.request().postDataJSON() as MoveRequest
    const from = positionFromKey(sourceKey)
    const to = positionFromKey(destinationKey)
    expect(submitted.from).toEqual(from)
    expect(submitted.to).toEqual(to)
    expect(submitted.expectedVersion).toBeGreaterThanOrEqual(0)
    expect(submitted.clientMoveId).toBeTruthy()
    return { from, to }
  }

  throw new Error('No enabled board piece exposed a candidate destination')
}

async function currentGame(player: PlayerClient, gameId: string): Promise<GameView> {
  const response = await player.context.request.get(`/api/games/${gameId}`)
  expect(response.ok()).toBeTruthy()
  return response.json() as Promise<GameView>
}

function assertCurrentViewEquivalent(wire: GameView | undefined, http: GameView): void {
  expect(wire).toBeDefined()
  const realtime = wire!
  expect({ ...realtime, clock: null }).toEqual({ ...http, clock: null })
  if (realtime.clock && http.clock) {
    expect(Math.abs(realtime.clock.redMilliseconds - http.clock.redMilliseconds)).toBeLessThan(1_000)
    expect(Math.abs(realtime.clock.blackMilliseconds - http.clock.blackMilliseconds)).toBeLessThan(1_000)
    expect(Math.abs(Date.parse(realtime.clock.serverTime) - Date.parse(http.clock.serverTime)))
      .toBeLessThan(1_000)
  } else {
    expect(realtime.clock).toBe(http.clock)
  }
}

async function expectResponsivePage(page: Page, testInfo: TestInfo): Promise<void> {
  if (testInfo.project.name !== 'mobile-chromium') return
  const dimensions = await page.evaluate(() => ({
    viewportWidth: window.innerWidth,
    documentWidth: document.documentElement.scrollWidth,
    bodyWidth: document.body.scrollWidth,
  }))
  expect(dimensions.viewportWidth).toBeLessThanOrEqual(430)
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth)
  expect(dimensions.bodyWidth).toBeLessThanOrEqual(dimensions.viewportWidth)
}

async function verifyReplayPage(page: Page, testInfo: TestInfo): Promise<void> {
  await expect(page).toHaveURL(/\/history\/[^/]+$/)
  await expect(page.getByRole('heading', { name: '迷雾棋局回放' })).toBeVisible()
  const board = page.getByTestId('game-board')
  await expect(board).toBeVisible()
  await expect(board.locator('svg.game-board__svg')).toHaveAttribute(
    'aria-label',
    /(红方|黑方)视野，第 0 个半回合/,
  )
  expect(await board.locator('[data-testid^="fog-"]').count()).toBeGreaterThan(0)

  await page.getByRole('button', { name: '全局视野' }).click()
  await expect(board.locator('[data-testid^="fog-"]')).toHaveCount(0)
  const slider = page.getByRole('slider', { name: '回放进度' })
  await expect(slider).toBeVisible()
  const finalFrame = await slider.getAttribute('max')
  expect(Number(finalFrame)).toBeGreaterThan(0)
  await page.getByRole('button', { name: '跳到终局' }).click()
  await expect(slider).toHaveValue(finalFrame as string)
  await expect(board.locator('svg.game-board__svg')).toHaveAttribute(
    'aria-label',
    /全局视野，第 \d+ 个半回合/,
  )
  await expect(page.getByText('初始局面')).toHaveCount(0)
  await expectResponsivePage(page, testInfo)
}

test('quick match completes through the UI with isolated realtime views and recovery', async ({ browser }, testInfo) => {
  const players = await openTwoPlayers(browser, testInfo)
  const [playerA, playerB] = players

  try {
    await expectResponsivePage(playerA.page, testInfo)
    await expectResponsivePage(playerB.page, testInfo)
    await restoreSessionByReload(playerA)
    await expect(playerA.page).toHaveURL(/\/$/)
    await expect(playerA.page.getByRole('button', { name: '寻找对手' })).toBeVisible()

    const firstTicketResponsePromise = waitForResponse(playerA.page, '/api/matchmaking/tickets')
    const lobbySocketPromise = waitForHubSocket(playerA.page, '/hubs/lobby')
    await playerA.page.getByRole('button', { name: '寻找对手' }).click()
    const firstTicketResponse = await firstTicketResponsePromise
    expect(firstTicketResponse.ok()).toBeTruthy()
    const firstTicket = await firstTicketResponse.json() as Record<string, unknown>
    expect(firstTicket.timeControl).toBe('600+5')
    const firstTicketProtocol = JSON.stringify(firstTicket)
    for (const internalField of [
      'eligiblePopulation',
      'populationBand',
      'waitingBonus',
      'effectiveRadius',
      'ratingSnapshot',
    ]) {
      expect(firstTicketProtocol).not.toContain(internalField)
    }
    await expect(playerA.page).toHaveURL(/\/match$/)

    const lobbySocket = await lobbySocketPromise
    expect(new URL(lobbySocket.url()).pathname).toBe('/hubs/lobby')
    const lobbyCapture = captureHub(lobbySocket)
    const lobbyConnection = playerA.page.locator('.connection-pill')
    await expect(lobbyConnection).toHaveAttribute('data-state', 'connected')
    await expect(lobbyConnection).toContainText('大厅已连接')
    await expectResponsivePage(playerA.page, testInfo)
    await expect(playerA.page.getByText('正在为你匹配对手…')).toBeVisible()

    const gameSocketAPromise = waitForHubSocket(playerA.page, '/hubs/game')
    const gameSocketBPromise = waitForHubSocket(playerB.page, '/hubs/game')
    const secondTicketResponsePromise = waitForResponse(playerB.page, '/api/matchmaking/tickets')
    await playerB.page.getByRole('button', { name: '寻找对手' }).click()
    const secondTicketResponse = await secondTicketResponsePromise
    expect(secondTicketResponse.ok()).toBeTruthy()

    await Promise.all([
      expect(playerA.page).toHaveURL(gamePathPattern),
      expect(playerB.page).toHaveURL(gamePathPattern),
    ])
    const gameId = gameIdFromPage(playerA.page)
    expect(gameIdFromPage(playerB.page)).toBe(gameId)
    await expect.poll(() => lobbyGameId(lobbyCapture)).toBe(gameId)

    const [gameSocketA, gameSocketB] = await Promise.all([
      gameSocketAPromise,
      gameSocketBPromise,
    ])
    expect(new URL(gameSocketA.url()).pathname).toBe('/hubs/game')
    expect(new URL(gameSocketB.url()).pathname).toBe('/hubs/game')
    const gameCaptureA = captureHub(gameSocketA)
    const gameCaptureB = captureHub(gameSocketB)

    await Promise.all([
      expect(playerA.page.getByTestId('game-board')).toBeVisible(),
      expect(playerB.page.getByTestId('game-board')).toBeVisible(),
      expectGameHubConnected(playerA.page),
      expectGameHubConnected(playerB.page),
    ])
    await expectResponsivePage(playerA.page, testInfo)
    await expectResponsivePage(playerB.page, testInfo)

    const [perspectiveA, perspectiveB] = await Promise.all([
      domPerspective(playerA.page),
      domPerspective(playerB.page),
    ])
    expect(perspectiveA).not.toBe(perspectiveB)
    const [initialVersionA, initialVersionB] = await Promise.all([
      domGameVersion(playerA.page),
      domGameVersion(playerB.page),
    ])
    expect(initialVersionA).toBe(initialVersionB)

    const playerATurn = await playerA.page.getByRole('heading', { name: '轮到你行棋' }).isVisible()
    const playerBTurn = await playerB.page.getByRole('heading', { name: '轮到你行棋' }).isVisible()
    expect(Number(playerATurn) + Number(playerBTurn)).toBe(1)
    const mover = playerATurn ? playerA : playerB
    const mutedPlayer = playerATurn ? playerB : playerA
    const moverBefore = await currentGame(mover, gameId)
    expect(moverBefore.clock).not.toBeNull()
    await expect.poll(() => playedSounds(mover.page)).toContain('game-start')
    await expect.poll(() => playedSounds(mutedPlayer.page)).toContain('game-start')
    const mutedSoundsBeforeMove = (await playedSounds(mutedPlayer.page)).length
    const muteButton = mutedPlayer.page.getByRole('button', { name: '音效开启' })
    await expect(muteButton).toHaveAttribute('aria-pressed', 'true')
    await muteButton.click()
    await expect(mutedPlayer.page.getByRole('button', { name: '音效静音' }))
      .toHaveAttribute('aria-pressed', 'false')
    await submitFirstBoardMove(mover.page, gameId)

    await Promise.all([
      expect.poll(() => domGameVersion(playerA.page)).toBeGreaterThan(initialVersionA),
      expect.poll(() => domGameVersion(playerB.page)).toBeGreaterThan(initialVersionB),
      expect.poll(() => gameViews(gameCaptureA).length).toBeGreaterThan(0),
      expect.poll(() => gameViews(gameCaptureB).length).toBeGreaterThan(0),
    ])
    const [movedVersionA, movedVersionB] = await Promise.all([
      domGameVersion(playerA.page),
      domGameVersion(playerB.page),
    ])
    expect(movedVersionA).toBe(movedVersionB)
    const moverAfter = await currentGame(mover, gameId)
    expect(moverAfter.clock).not.toBeNull()
    const beforeMoverMilliseconds = moverBefore.sideToMove === 'red'
      ? moverBefore.clock!.redMilliseconds
      : moverBefore.clock!.blackMilliseconds
    const afterMoverMilliseconds = moverBefore.sideToMove === 'red'
      ? moverAfter.clock!.redMilliseconds
      : moverAfter.clock!.blackMilliseconds
    expect(afterMoverMilliseconds - beforeMoverMilliseconds).toBeGreaterThan(2_500)
    expect(afterMoverMilliseconds - beforeMoverMilliseconds).toBeLessThanOrEqual(5_000)
    await expect.poll(() => playedSounds(mover.page)).toContain('move-self')
    expect(await playedSounds(mutedPlayer.page)).toHaveLength(mutedSoundsBeforeMove)
    await mutedPlayer.page.getByRole('button', { name: '音效静音' }).click()
    await expect(mutedPlayer.page.getByRole('button', { name: '音效开启' }))
      .toHaveAttribute('aria-pressed', 'true')

    const [viewA, viewB, fogA, fogB] = await Promise.all([
      currentGame(playerA, gameId),
      currentGame(playerB, gameId),
      domFogSquares(playerA.page),
      domFogSquares(playerB.page),
    ])
    const wireViewA = latestGameView(gameCaptureA)
    const wireViewB = latestGameView(gameCaptureB)
    assertCurrentViewEquivalent(wireViewA, viewA)
    assertCurrentViewEquivalent(wireViewB, viewB)
    expect(viewA.perspective).toBe(perspectiveA)
    expect(viewB.perspective).toBe(perspectiveB)
    expect(viewA.visibleSquares).not.toEqual(viewB.visibleSquares)
    expect(fogA.length).toBeGreaterThan(0)
    expect(fogB.length).toBeGreaterThan(0)
    expect(fogA).not.toEqual(fogB)
    expect(fogA).toEqual(fogSquaresFromView(viewA))
    expect(fogB).toEqual(fogSquaresFromView(viewB))
    assertPlayerScopedProtocol([gameCaptureA], perspectiveA, gameId)
    assertPlayerScopedProtocol([gameCaptureB], perspectiveB, gameId)

    const offlineNotice = playerB.page.getByRole('status').filter({ hasText: '对手暂时离线' })
    const gameUrl = playerA.page.url()
    await playerA.page.close()
    await expect(offlineNotice).toBeVisible({ timeout: 15_000 })

    const recoveredSockets: WebSocket[] = []
    const trackRecoveredSocket = (socket: WebSocket) => {
      if (new URL(socket.url()).pathname === '/hubs/game') recoveredSockets.push(socket)
    }
    playerA.page = await playerA.context.newPage()
    playerA.page.on('websocket', trackRecoveredSocket)
    const restoredSessionResponsePromise = waitForResponse(playerA.page, '/api/sessions/guest')
    await playerA.page.goto(gameUrl)
    const restoredSessionResponse = await restoredSessionResponsePromise
    expect(restoredSessionResponse.ok()).toBeTruthy()
    const restoredSession = await restoredSessionResponse.json() as GuestSession
    expect(restoredSession.playerId).toBe(playerA.session.playerId)
    await expect(playerA.page.getByTestId('game-board')).toBeVisible()
    await expectGameHubConnected(playerA.page)
    await expect.poll(() => recoveredSockets.filter((socket) => !socket.isClosed()).length)
      .toBeGreaterThan(0)
    playerA.page.off('websocket', trackRecoveredSocket)
    const recoveredSocket = recoveredSockets.findLast((socket) => !socket.isClosed())
    if (!recoveredSocket) throw new Error('Reopened game did not establish a live GameHub socket')
    const recoveredCaptureA = captureHub(recoveredSocket)
    expect(await domGameVersion(playerA.page)).toBe(movedVersionA)
    await expect(offlineNotice).toHaveCount(0)
    await expectResponsivePage(playerA.page, testInfo)

    await playerA.page.getByRole('button', { name: '认输', exact: true }).click()
    const resignDialog = playerA.page.getByRole('alertdialog', { name: '确认认输' })
    await expect(resignDialog).toBeVisible()
    const resignResponsePromise = waitForResponse(playerA.page, `/api/games/${gameId}/resign`)
    await resignDialog.getByRole('button', { name: '确认认输' }).click()
    const resignResponse = await resignResponsePromise
    expect(resignResponse.ok()).toBeTruthy()

    await Promise.all([
      expect(playerA.page.getByRole('heading', { name: '棋局结束' })).toBeVisible(),
      expect(playerB.page.getByRole('heading', { name: '棋局结束' })).toBeVisible(),
      expect(playerA.page.getByText('认输', { exact: true })).toBeVisible(),
      expect(playerB.page.getByText('认输', { exact: true })).toBeVisible(),
      expect.poll(() => hubArguments<GameView>(recoveredCaptureA, ['GameEnded']).length)
        .toBeGreaterThan(0),
      expect.poll(() => hubArguments<GameView>(gameCaptureB, ['GameEnded']).length)
        .toBeGreaterThan(0),
    ])
    await expect.poll(() => playedSounds(playerA.page)).toContain('game-loss')
    await expect.poll(() => playedSounds(playerB.page)).toContain('game-win')
    const rematchResponsePromise = waitForResponse(
      playerB.page,
      '/api/matchmaking/tickets',
    )
    await playerB.page.getByRole('button', { name: '重新匹配' }).click()
    const rematchResponse = await rematchResponsePromise
    expect(rematchResponse.ok()).toBeTruthy()
    const rematchTicket = await rematchResponse.json() as {
      ticketId: string
      timeControl: string
    }
    expect(rematchTicket.timeControl).toBe('600+5')
    await expect(playerB.page).toHaveURL(/\/match$/)
    await expect(playerB.page.getByText('正在为你匹配对手…')).toBeVisible()
    const currentTicketResponse = await playerB.context.request.get(
      '/api/matchmaking/tickets/current',
    )
    expect(currentTicketResponse.ok()).toBeTruthy()
    const currentTicket = await currentTicketResponse.json() as { ticketId: string }
    expect(currentTicket.ticketId).toBe(rematchTicket.ticketId)
    const cancelResponsePromise = waitForResponse(
      playerB.page,
      `/api/matchmaking/tickets/${rematchTicket.ticketId}`,
      'DELETE',
    )
    await playerB.page.getByRole('button', { name: '取消匹配' }).click()
    expect((await cancelResponsePromise).ok()).toBeTruthy()
    await playerB.page.goto(`${baseUrl}/game/${gameId}`)
    await expect(playerB.page.getByRole('heading', { name: '棋局结束' })).toBeVisible()
    assertPlayerScopedProtocol([gameCaptureA, recoveredCaptureA], perspectiveA, gameId)
    assertPlayerScopedProtocol([gameCaptureB], perspectiveB, gameId)

    const lobbyProtocol = lobbyCapture.rawFrames.join('\n')
    for (const internalField of [
      'eligiblePopulation',
      'populationBand',
      'waitingBonus',
      'effectiveRadius',
      'ratingSnapshot',
    ]) {
      expect(lobbyProtocol).not.toContain(internalField)
    }

    await Promise.all([
      playerA.page.getByRole('link', { name: '查看完整回放' }).click(),
      playerB.page.getByRole('link', { name: '查看完整回放' }).click(),
    ])
    await Promise.all([
      verifyReplayPage(playerA.page, testInfo),
      verifyReplayPage(playerB.page, testInfo),
    ])

    const createShareResponsePromise = waitForResponse(
      playerA.page,
      `/api/games/${gameId}/replay-share`,
    )
    await playerA.page.getByRole('button', { name: '生成分享链接' }).click()
    expect((await createShareResponsePromise).ok()).toBeTruthy()
    const shareUrl = await playerA.page.getByRole('textbox', { name: '分享链接' }).inputValue()
    expect(shareUrl).toMatch(/\/shared\/replay\/[A-Za-z0-9_-]{43}$/)

    const sharedContext = await browser.newContext()
    try {
      const sharedPage = await sharedContext.newPage()
      let guestSessionRequests = 0
      sharedPage.on('request', (request) => {
        if (new URL(request.url()).pathname === '/api/sessions/guest') {
          guestSessionRequests += 1
        }
      })
      await sharedPage.goto(shareUrl)
      await expect(sharedPage.getByRole('heading', { name: '迷雾棋局回放' })).toBeVisible()
      await expect(sharedPage.getByText(/通过分享链接观看。此链接只授予/)).toBeVisible()
      expect(guestSessionRequests).toBe(0)
      await expectResponsivePage(sharedPage, testInfo)

      const revokeResponsePromise = waitForResponse(
        playerA.page,
        `/api/games/${gameId}/replay-share`,
        'DELETE',
      )
      await playerA.page.getByRole('button', { name: '撤销当前分享' }).click()
      expect((await revokeResponsePromise).status()).toBe(204)
      await sharedPage.reload()
      await expect(
        sharedPage.getByRole('heading', { name: '分享链接无效或已撤销' }),
      ).toBeVisible()
    } finally {
      await sharedContext.close()
    }

    await playerA.page.getByRole('link', { name: '返回历史列表' }).click()
    await expect(playerA.page.getByRole('heading', { name: '我的历史对局' })).toBeVisible()
    await expect(playerA.page.getByRole('link', { name: /查看回放/ }).first()).toBeVisible()
    await expectResponsivePage(playerA.page, testInfo)
  } finally {
    await Promise.all(players.map((player) => player.context.close()))
  }
})

test('private room is created, joined, and started through page controls', async ({ browser }, testInfo) => {
  const players = await openTwoPlayers(browser, testInfo)
  const [creator, joiner] = players

  try {
    const createResponsePromise = waitForResponse(creator.page, '/api/rooms')
    await creator.page.getByRole('button', { name: '创建房间' }).click()
    const createResponse = await createResponsePromise
    expect(createResponse.ok()).toBeTruthy()
    await expect(creator.page).toHaveURL(/\/room\/[^/]+$/)
    await expect(creator.page.getByRole('heading', { name: '好友对局房间' })).toBeVisible()

    const roomCodeLabel = creator.page.locator('[aria-label^="房间码 "]')
    const roomCodeText = await roomCodeLabel.getAttribute('aria-label')
    const roomCode = roomCodeText?.replace(/^房间码 /, '') ?? ''
    expect(roomCode).toMatch(/^[A-Z0-9]{8}$/)
    await expectResponsivePage(creator.page, testInfo)

    await joiner.page.locator('form.room-code-form #room-code').fill(roomCode)
    const joinResponsePromise = waitForResponse(joiner.page, `/api/rooms/${roomCode}/join`)
    await joiner.page.getByRole('button', { name: '加入', exact: true }).click()
    const joinResponse = await joinResponsePromise
    expect(joinResponse.ok()).toBeTruthy()
    await expect(joiner.page).toHaveURL(new RegExp(`/room/${roomCode}$`))
    await expect(joiner.page.locator(`[aria-label="房间码 ${roomCode}"]`)).toBeVisible()
    await expect(creator.page.getByText('等待玩家加入')).toHaveCount(0, { timeout: 15_000 })
    await expectResponsivePage(joiner.page, testInfo)

    const creatorReadyResponsePromise = waitForResponse(
      creator.page,
      `/api/rooms/${roomCode}/ready`,
    )
    await creator.page.getByRole('button', { name: '我已准备' }).click()
    const creatorReadyResponse = await creatorReadyResponsePromise
    expect(creatorReadyResponse.ok()).toBeTruthy()
    await expect(creator.page.getByRole('button', { name: '取消准备' })).toBeVisible()

    const creatorGameSocketPromise = waitForHubSocket(creator.page, '/hubs/game')
    const joinerGameSocketPromise = waitForHubSocket(joiner.page, '/hubs/game')
    const joinerReadyResponsePromise = waitForResponse(
      joiner.page,
      `/api/rooms/${roomCode}/ready`,
    )
    await joiner.page.getByRole('button', { name: '我已准备' }).click()
    const joinerReadyResponse = await joinerReadyResponsePromise
    expect(joinerReadyResponse.ok()).toBeTruthy()

    await Promise.all([
      expect(creator.page).toHaveURL(gamePathPattern, { timeout: 15_000 }),
      expect(joiner.page).toHaveURL(gamePathPattern, { timeout: 15_000 }),
    ])
    const gameId = gameIdFromPage(creator.page)
    expect(gameIdFromPage(joiner.page)).toBe(gameId)

    const [creatorGameSocket, joinerGameSocket] = await Promise.all([
      creatorGameSocketPromise,
      joinerGameSocketPromise,
    ])
    expect(new URL(creatorGameSocket.url()).pathname).toBe('/hubs/game')
    expect(new URL(joinerGameSocket.url()).pathname).toBe('/hubs/game')
    await Promise.all([
      expect(creator.page.getByTestId('game-board')).toBeVisible(),
      expect(joiner.page.getByTestId('game-board')).toBeVisible(),
      expectGameHubConnected(creator.page),
      expectGameHubConnected(joiner.page),
    ])
    const [creatorPerspective, joinerPerspective] = await Promise.all([
      domPerspective(creator.page),
      domPerspective(joiner.page),
    ])
    expect(creatorPerspective).not.toBe(joinerPerspective)
    await expectResponsivePage(creator.page, testInfo)
    await expectResponsivePage(joiner.page, testInfo)
  } finally {
    await Promise.all(players.map((player) => player.context.close()))
  }
})
