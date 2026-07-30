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

const baseUrl = process.env.MISTCHESS_E2E_BASE_URL?.trim() || 'http://127.0.0.1:5173'
const gamePathPattern = /\/game\/([^/?#]+)$/
const e2eAdminUsername = process.env.MISTCHESS_E2E_ADMIN_USERNAME?.trim()
const e2eAdminPassword = process.env.MISTCHESS_E2E_ADMIN_PASSWORD
if (process.env.TEST_WORKER_INDEX !== undefined) {
  delete process.env.MISTCHESS_E2E_ADMIN_PASSWORD
}

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

async function submitBoardMove(
  page: Page,
  gameId: string,
  from: string,
  to: string,
): Promise<void> {
  const board = page.getByTestId('game-board')
  const source = board.locator(
    `button[data-position="${from}"]:not([data-candidate])`,
  )
  await expect(source).toBeEnabled()
  await source.click()
  const destination = board.locator(
    `button[data-position="${to}"][data-candidate="true"]`,
  )
  await expect(destination).toBeEnabled()

  const responsePromise = waitForResponse(page, `/api/games/${gameId}/moves`)
  await destination.click()
  const response = await responsePromise
  expect(response.ok()).toBeTruthy()
  const submitted = response.request().postDataJSON() as MoveRequest
  expect(submitted.from).toEqual(positionFromKey(from))
  expect(submitted.to).toEqual(positionFromKey(to))
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

async function expectBoardViewportScaling(page: Page): Promise<void> {
  const geometry = await page.getByTestId('game-board').evaluate((board) => {
    const target = board.querySelector<HTMLButtonElement>(
      'button[data-position]:not([data-candidate])',
    )
    const position = target?.dataset.position
    const piece = position
      ? board.querySelector<SVGGElement>(`[data-testid="piece-${position}"]`)
      : null
    const scaledPiece = piece?.querySelector<SVGGElement>('.board-horizontal-scale')
    if (!scaledPiece || !target) return null

    const boardBounds = board.getBoundingClientRect()
    const pieceBounds = scaledPiece.getBoundingClientRect()
    const targetBounds = target.getBoundingClientRect()
    const transform = getComputedStyle(scaledPiece).transform
    const matrix = transform === 'none' ? new DOMMatrixReadOnly() : new DOMMatrixReadOnly(transform)
    return {
      aspectRatio: boardBounds.width / boardBounds.height,
      pieceScaleX: matrix.a,
      portraitRuleActive: matchMedia(
        '(max-width: 780px) and (orientation: portrait)',
      ).matches,
      centerDeltaX: Math.abs(
        pieceBounds.left + pieceBounds.width / 2
          - targetBounds.left - targetBounds.width / 2,
      ),
      centerDeltaY: Math.abs(
        pieceBounds.top + pieceBounds.height / 2
          - targetBounds.top - targetBounds.height / 2,
      ),
    }
  })

  expect(geometry).not.toBeNull()
  const portrait = geometry!.portraitRuleActive
  expect(geometry!.aspectRatio).toBeCloseTo(portrait ? 5 / 6 : 1, 1)
  expect(geometry!.pieceScaleX).toBeCloseTo(portrait ? 1.2 : 1, 2)
  expect(geometry!.centerDeltaX).toBeLessThan(1)
  expect(geometry!.centerDeltaY).toBeLessThan(1)
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

test('real browsers decode and start the distinct move and capture audio assets', async ({ page }) => {
  await page.goto(baseUrl)
  await expect(page.getByRole('button', { name: '寻找对手' })).toBeVisible()

  const decoded = await page.evaluate(async () => {
    const probe = document.createElement('audio')
    const playableExtension = probe.canPlayType('audio/ogg; codecs="vorbis"') ? 'ogg' : 'mp3'
    const playableSources = [
      `/audio/move.${playableExtension}`,
      `/audio/capture.${playableExtension}`,
    ]
    const sources = [
      '/audio/move.ogg',
      '/audio/capture.ogg',
      '/audio/move.mp3',
      '/audio/capture.mp3',
    ]
    const audioContext = new AudioContext()
    try {
      const metrics = await Promise.all(sources.map(async (source) => {
        const response = await fetch(source)
        if (!response.ok) throw new Error(`Audio asset failed to load: ${source}`)
        const buffer = await audioContext.decodeAudioData(await response.arrayBuffer())
        const samples = buffer.getChannelData(0)
        let peak = 0
        let energy = 0
        for (const sample of samples) {
          peak = Math.max(peak, Math.abs(sample))
          energy += sample * sample
        }
        return {
          source,
          duration: buffer.duration,
          peak,
          rms: Math.sqrt(energy / samples.length),
        }
      }))
      return { playableSources, metrics }
    } finally {
      await audioContext.close()
    }
  })

  expect(decoded.metrics).toHaveLength(4)
  for (const metric of decoded.metrics) {
    expect(metric.duration).toBeGreaterThan(0.1)
    expect(metric.peak).toBeGreaterThan(0.2)
  }
  for (const extension of ['ogg', 'mp3']) {
    const move = decoded.metrics.find((metric) => metric.source === `/audio/move.${extension}`)
    const capture = decoded.metrics.find(
      (metric) => metric.source === `/audio/capture.${extension}`,
    )
    expect(move).toBeDefined()
    expect(capture).toBeDefined()
    expect(capture!.duration).toBeGreaterThan(move!.duration * 1.5)
    expect(move!.rms).not.toBeCloseTo(capture!.rms, 2)
  }

  await page.evaluate((sources) => {
    const button = document.createElement('button')
    button.textContent = '播放验证音效'
    button.addEventListener('click', () => {
      const players = sources.map((source) => new Audio(source))
      ;(window as Window & { __audioPlayback?: Promise<string[]> }).__audioPlayback =
        Promise.all(players.map(async (player) => {
          await player.play()
          const resolvedSource = player.currentSrc
          player.pause()
          return resolvedSource
        }))
    })
    document.body.append(button)
  }, decoded.playableSources)
  await page.getByRole('button', { name: '播放验证音效' }).click()
  const playbackSources = await page.evaluate(() =>
    (window as Window & { __audioPlayback?: Promise<string[]> }).__audioPlayback)
  expect(playbackSources).toHaveLength(2)
  expect(playbackSources?.every((source) => source.startsWith('http'))).toBe(true)
})

test('home page omits the promotional copy and rule footer', async ({ browser }, testInfo) => {
  const context = await browser.newContext(browserContextOptions(testInfo, 0))
  const page = await context.newPage()

  try {
    await page.goto(baseUrl)
    await expect(page.getByRole('button', { name: '寻找对手' })).toBeVisible()
    for (const removedText of [
      '标准中国象棋规则',
      '双人实时',
      '无需注册',
      '完整回放',
      '己方棋子与行动路线提供视野',
      '每步后视野重算',
      '只按服务器候选落点行动',
      '服务端权威裁定',
    ]) {
      await expect(page.getByText(removedText, { exact: false })).toHaveCount(0)
    }
    await expectResponsivePage(page, testInfo)
    await page.screenshot({
      path: testInfo.outputPath('home-without-promotional-copy.png'),
      fullPage: true,
    })
  } finally {
    await context.close()
  }
})

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
    await expectBoardViewportScaling(playerA.page)
    await expectBoardViewportScaling(playerB.page)

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

test('mobile portrait completes a normal move and capture without landscape regression', async ({
  browser,
}, testInfo) => {
  test.skip(
    testInfo.project.name !== 'mobile-chromium',
    'The portrait capture scenario runs in the Pixel 5 project.',
  )
  const players = await openTwoPlayers(browser, testInfo)
  const [first, second] = players

  try {
    await first.page.getByRole('button', { name: '寻找对手' }).click()
    await expect(first.page.getByRole('heading', { name: '正在为你匹配对手…' }))
      .toBeVisible()
    await second.page.getByRole('button', { name: '寻找对手' }).click()
    await Promise.all([
      expect(first.page).toHaveURL(gamePathPattern),
      expect(second.page).toHaveURL(gamePathPattern),
      expect(first.page.getByTestId('game-board')).toBeVisible(),
      expect(second.page.getByTestId('game-board')).toBeVisible(),
    ])

    const gameId = gameIdFromPage(first.page)
    const firstPerspective = await domPerspective(first.page)
    const red = firstPerspective === 'red' ? first : second
    const black = firstPerspective === 'black' ? first : second
    await expectBoardViewportScaling(red.page)
    await expectBoardViewportScaling(black.page)

    await submitBoardMove(red.page, gameId, '0:3', '0:4')
    await expect(black.page.getByRole('heading', { name: '轮到你行棋' })).toBeVisible()
    await submitBoardMove(black.page, gameId, '0:6', '0:5')
    await expect(red.page.getByRole('heading', { name: '轮到你行棋' })).toBeVisible()
    await submitBoardMove(red.page, gameId, '0:4', '0:5')

    await expect.poll(() => playedSounds(red.page)).toContain('move-self')
    await expect.poll(() => playedSounds(red.page)).toContain('capture')
    await expect.poll(() => playedSounds(black.page)).toContain('move-self')
    await expect.poll(() => playedSounds(black.page)).toContain('capture')

    await red.page.setViewportSize({ width: 740, height: 393 })
    const landscapeMedia = await red.page.evaluate(() => ({
      compact: matchMedia('(max-width: 780px)').matches,
      portrait: matchMedia('(max-width: 780px) and (orientation: portrait)').matches,
      landscape: matchMedia('(orientation: landscape)').matches,
    }))
    expect(landscapeMedia).toEqual({ compact: true, portrait: false, landscape: true })
    await expectBoardViewportScaling(red.page)
    const landscapeWidth = await red.page.evaluate(() => ({
      viewport: window.innerWidth,
      document: document.documentElement.scrollWidth,
      body: document.body.scrollWidth,
    }))
    expect(landscapeWidth.document).toBeLessThanOrEqual(landscapeWidth.viewport)
    expect(landscapeWidth.body).toBeLessThanOrEqual(landscapeWidth.viewport)
  } finally {
    await Promise.all(players.map((player) => player.context.close()))
  }
})

test('private room is created, joined, and started through page controls', async ({ browser }, testInfo) => {
  const players = await openTwoPlayers(browser, testInfo)
  const [creator, joiner] = players

  try {
    const moveTimeLimit = creator.page.getByLabel('单步上限')
    await expect(moveTimeLimit).toHaveValue('90')
    await moveTimeLimit.selectOption('60')
    const createRequestPromise = creator.page.waitForRequest((request) =>
      new URL(request.url()).pathname === '/api/rooms' && request.method() === 'POST')
    const createResponsePromise = waitForResponse(creator.page, '/api/rooms')
    await creator.page.getByRole('button', { name: '创建房间' }).click()
    const [createRequest, createResponse] = await Promise.all([
      createRequestPromise,
      createResponsePromise,
    ])
    expect(createRequest.postDataJSON()).toMatchObject({
      timeControl: '180+2',
      moveTimeLimitSeconds: 60,
    })
    expect(createResponse.ok()).toBeTruthy()
    expect((await createResponse.json()).moveTimeLimitSeconds).toBe(60)
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

test.describe('administrator player lifecycle', () => {
  test('an administrator can ban an active player, review the result, and restore access', async ({
    browser,
  }, testInfo) => {
    test.skip(
      !e2eAdminUsername,
      'MISTCHESS_E2E_ADMIN_USERNAME is required for the administrator lifecycle scenario.',
    )
    test.skip(
      !e2eAdminPassword,
      'MISTCHESS_E2E_ADMIN_PASSWORD is required for the administrator lifecycle scenario.',
    )
    if (!e2eAdminUsername || !e2eAdminPassword) return

    let players: [PlayerClient, PlayerClient] | undefined
    let adminContext: BrowserContext | undefined

    try {
      players = await openTwoPlayers(browser, testInfo)
      const [bannedPlayer, opponent] = players

      const firstLobbySocketPromise = waitForHubSocket(bannedPlayer.page, '/hubs/lobby')
      await bannedPlayer.page.getByRole('button', { name: '寻找对手' }).click()
      const firstLobbySocket = await firstLobbySocketPromise
      expect(new URL(firstLobbySocket.url()).pathname).toBe('/hubs/lobby')
      await expect(
        bannedPlayer.page.getByRole('heading', { name: '正在为你匹配对手…' }),
      ).toBeVisible()

      const bannedPlayerGameSocketPromise = waitForHubSocket(bannedPlayer.page, '/hubs/game')
      const opponentGameSocketPromise = waitForHubSocket(opponent.page, '/hubs/game')
      await opponent.page.getByRole('button', { name: '寻找对手' }).click()

      await Promise.all([
        expect(bannedPlayer.page).toHaveURL(gamePathPattern),
        expect(opponent.page).toHaveURL(gamePathPattern),
      ])
      const gameId = gameIdFromPage(bannedPlayer.page)
      expect(gameIdFromPage(opponent.page)).toBe(gameId)

      const [bannedPlayerGameSocket, opponentGameSocket] = await Promise.all([
        bannedPlayerGameSocketPromise,
        opponentGameSocketPromise,
      ])
      expect(new URL(bannedPlayerGameSocket.url()).pathname).toBe('/hubs/game')
      expect(new URL(opponentGameSocket.url()).pathname).toBe('/hubs/game')
      await Promise.all([
        expect(bannedPlayer.page.getByTestId('game-board')).toBeVisible(),
        expect(opponent.page.getByTestId('game-board')).toBeVisible(),
        expectGameHubConnected(bannedPlayer.page),
        expectGameHubConnected(opponent.page),
      ])
      await Promise.all([
        expectResponsivePage(bannedPlayer.page, testInfo),
        expectResponsivePage(opponent.page, testInfo),
      ])

      const bannedSide = await domPerspective(bannedPlayer.page)
      const winningSide = bannedSide === 'red' ? 'black' : 'red'
      const winningSideName = winningSide === 'red' ? '红方' : '黑方'
      const banReason = '管理员端到端验证：破坏公平对局。'

      adminContext = await browser.newContext(browserContextOptions(testInfo, 2))
      const adminPage = await adminContext.newPage()
      await adminPage.goto('/admin/login')
      await expect(adminPage.getByRole('heading', { name: '管理员登录' })).toBeVisible()
      await adminPage.getByRole('textbox', { name: '用户名' }).fill(e2eAdminUsername)
      await adminPage.getByLabel('密码').fill(e2eAdminPassword)
      await adminPage.getByRole('button', { name: '进入管理后台' }).click()
      await expect(adminPage).toHaveURL(/\/admin\/users$/)
      await expect(adminPage.getByRole('heading', { name: '用户管理' })).toBeVisible()
      await expectResponsivePage(adminPage, testInfo)
      await adminPage.getByRole('link', { name: '当前在线' }).click()
      await expect(adminPage).toHaveURL(/\/admin\/users\?online=online$/)
      await expect(
        adminPage.getByRole('link', { name: '当前在线' }),
      ).toHaveAttribute('aria-current', 'page')

      await adminPage
        .getByRole('searchbox', { name: '名称或用户 ID' })
        .fill(bannedPlayer.session.playerId)
      await adminPage.getByRole('button', { name: '搜索', exact: true }).click()
      const targetRow = adminPage.getByRole('row', {
        name: new RegExp(bannedPlayer.session.playerId),
      })
      await expect(targetRow).toBeVisible()
      await targetRow.getByRole('link', { name: '查看详情' }).click()
      await expect(
        adminPage.getByRole('heading', { name: bannedPlayer.session.displayName }),
      ).toBeVisible()
      await expect(adminPage.getByText(bannedPlayer.session.playerId, { exact: true })).toBeVisible()

      await adminPage.getByRole('button', { name: '封禁用户' }).click()
      const banDialog = adminPage.getByRole('dialog', {
        name: `确认封禁 ${bannedPlayer.session.displayName}`,
      })
      await expect(banDialog).toBeVisible()
      await banDialog.getByLabel('封禁原因（1–200 字）').fill(banReason)
      await banDialog.getByRole('button', { name: '确认封禁' }).click()

      await Promise.all([
        expect(adminPage.getByText('用户已封禁。', { exact: true })).toBeVisible(),
        expect(
          bannedPlayer.page.getByRole('heading', { name: '账号已被封禁' }),
        ).toBeVisible(),
        expect(bannedPlayer.page.getByText(banReason, { exact: true })).toBeVisible(),
        expect(opponent.page.getByRole('heading', { name: '棋局结束' })).toBeVisible(),
        expect(
          opponent.page.getByRole('heading', { name: `${winningSideName}获胜` }),
        ).toBeVisible(),
        expect(opponent.page.getByText('管理员判负', { exact: true })).toBeVisible(),
      ])

      await expect(adminPage.getByText('管理员封禁判负', { exact: true })).toBeVisible()
      const replayLink = adminPage.getByRole('link', { name: '查看三视野回放' })
      await expect(replayLink).toBeVisible()
      await replayLink.click()
      await expect(adminPage).toHaveURL(new RegExp(`/admin/games/${gameId}$`))
      await expect(adminPage.getByRole('heading', { name: '管理员棋局回放' })).toBeVisible()
      await expect(adminPage.getByText('管理员封禁判负', { exact: true })).toBeVisible()

      const replayBoard = adminPage.getByTestId('game-board')
      const replayModes = adminPage.getByRole('group', { name: '回放视野' })
      const replaySlider = adminPage.getByRole('slider', { name: '回放进度' })
      const finalFrame = await replaySlider.getAttribute('max')
      expect(Number(finalFrame)).toBeGreaterThan(0)
      await adminPage.getByRole('button', { name: '跳到终局' }).click()
      await expect(replaySlider).toHaveValue(finalFrame as string)

      await replayModes.getByRole('button', { name: '红方视野' }).click()
      await expect(replayBoard.locator('svg.game-board__svg')).toHaveAttribute(
        'aria-label',
        /红方视野，第 \d+ 个半回合/,
      )
      expect(await replayBoard.locator('[data-testid^="fog-"]').count()).toBeGreaterThan(0)

      await replayModes.getByRole('button', { name: '黑方视野' }).click()
      await expect(replayBoard.locator('svg.game-board__svg')).toHaveAttribute(
        'aria-label',
        /黑方视野，第 \d+ 个半回合/,
      )
      expect(await replayBoard.locator('[data-testid^="fog-"]').count()).toBeGreaterThan(0)

      await replayModes.getByRole('button', { name: '全局视野' }).click()
      await expect(replayBoard.locator('svg.game-board__svg')).toHaveAttribute(
        'aria-label',
        /全局视野，第 \d+ 个半回合/,
      )
      await expect(replayBoard.locator('[data-testid^="fog-"]')).toHaveCount(0)
      await expectResponsivePage(adminPage, testInfo)

      await adminPage.goBack()
      await expect(
        adminPage.getByRole('heading', { name: bannedPlayer.session.displayName }),
      ).toBeVisible()
      await adminPage.getByRole('button', { name: '解除封禁' }).click()
      const unbanDialog = adminPage.getByRole('dialog', {
        name: `确认解封 ${bannedPlayer.session.displayName}`,
      })
      await expect(unbanDialog).toBeVisible()
      await unbanDialog.getByRole('button', { name: '确认解封' }).click()
      await expect(adminPage.getByText('用户已解除封禁。', { exact: true })).toBeVisible()
      await expect(adminPage.getByRole('button', { name: '封禁用户' })).toBeVisible()

      await bannedPlayer.page.reload()
      await expect(
        bannedPlayer.page.getByRole('heading', { name: '账号已被封禁' }),
      ).toHaveCount(0)
      await expect(bannedPlayer.page.getByRole('heading', { name: '棋局结束' })).toBeVisible()
      await expectGameHubConnected(bannedPlayer.page)
      await adminPage.getByRole('button', { name: '封禁用户' }).click()
      await expect(banDialog).toBeVisible()
      await banDialog.getByLabel('封禁原因（1–200 字）')
        .fill('管理员端到端验证：已结束棋局连接。')
      await banDialog.getByRole('button', { name: '确认封禁' }).click()
      await expect(
        bannedPlayer.page.getByRole('heading', { name: '账号已被封禁' }),
      ).toBeVisible()

      await adminPage.getByRole('button', { name: '解除封禁' }).click()
      await expect(unbanDialog).toBeVisible()
      await unbanDialog.getByRole('button', { name: '确认解封' }).click()
      await expect(adminPage.getByText('用户已解除封禁。', { exact: true })).toBeVisible()
      await bannedPlayer.page.reload()
      await expect(bannedPlayer.page.getByRole('heading', { name: '棋局结束' })).toBeVisible()
      await bannedPlayer.page.getByRole('link', { name: '迷雾象棋首页' }).click()
      await expect(bannedPlayer.page).toHaveURL(/\/$/)
      await expect(bannedPlayer.page.getByRole('heading', { name: '快速匹配' })).toBeVisible()
      await expect(bannedPlayer.page.getByRole('button', { name: '寻找对手' })).toBeVisible()

      const restoredLobbySocketPromise = waitForHubSocket(bannedPlayer.page, '/hubs/lobby')
      await bannedPlayer.page.getByRole('button', { name: '寻找对手' }).click()
      const restoredLobbySocket = await restoredLobbySocketPromise
      expect(new URL(restoredLobbySocket.url()).pathname).toBe('/hubs/lobby')
      await expect(
        bannedPlayer.page.getByRole('heading', { name: '正在为你匹配对手…' }),
      ).toBeVisible()
      await expect(bannedPlayer.page.locator('.connection-pill')).toContainText('大厅已连接')
      const restoredCancelResponsePromise = bannedPlayer.page.waitForResponse((response) => {
        const path = new URL(response.url()).pathname
        return path.startsWith('/api/matchmaking/tickets/')
          && response.request().method() === 'DELETE'
      })
      await bannedPlayer.page.getByRole('button', { name: '取消匹配' }).click()
      expect((await restoredCancelResponsePromise).ok()).toBeTruthy()
      await expect(bannedPlayer.page).toHaveURL(/\/$/)
      await expect(
        bannedPlayer.page.getByRole('button', { name: '寻找对手' }),
      ).toBeVisible()
      await expectResponsivePage(bannedPlayer.page, testInfo)
    } finally {
      await Promise.all([
        ...(players?.map((player) => player.context.close()) ?? []),
        ...(adminContext ? [adminContext.close()] : []),
      ])
    }
  })
})
