import { useEffect } from 'react'
import { BrowserRouter, Outlet, Route, Routes } from 'react-router'
import { AppShell } from './components/AppShell'
import { SessionGate } from './features/session/SessionGate'
import { AdminGate } from './features/admin/AdminGate'
import { AdminLayout } from './features/admin/AdminLayout'
import { audioService } from './features/audio/audioService'
import { GamePage } from './routes/GamePage'
import { HomePage } from './routes/HomePage'
import { HistoryPage } from './routes/HistoryPage'
import { MatchPage } from './routes/MatchPage'
import { ReplayPage } from './routes/ReplayPage'
import { RoomPage } from './routes/RoomPage'
import { AdminLoginPage } from './routes/admin/AdminLoginPage'
import { AdminReplayPage } from './routes/admin/AdminReplayPage'
import { AdminRootPage } from './routes/admin/AdminRootPage'
import { AdminUserDetailPage } from './routes/admin/AdminUserDetailPage'
import { AdminUsersPage } from './routes/admin/AdminUsersPage'

function App() {
  useEffect(() => {
    const unlock = () => void audioService.unlock()
    window.addEventListener('pointerdown', unlock)
    window.addEventListener('keydown', unlock)
    return () => {
      window.removeEventListener('pointerdown', unlock)
      window.removeEventListener('keydown', unlock)
    }
  }, [])

  return (
    <BrowserRouter>
      <Routes>
        <Route path="admin" element={<AdminLayout />}>
          <Route index element={<AdminRootPage />} />
          <Route path="login" element={<AdminLoginPage />} />
          <Route element={<AdminGate />}>
            <Route path="users" element={<AdminUsersPage />} />
            <Route path="users/:playerId" element={<AdminUserDetailPage />} />
            <Route path="games/:gameId" element={<AdminReplayPage />} />
          </Route>
        </Route>
        <Route element={<AppShell />}>
          <Route path="shared/replay/:shareToken" element={<ReplayPage shared />} />
          <Route element={<SessionGate><Outlet /></SessionGate>}>
            <Route index element={<HomePage />} />
            <Route path="match" element={<MatchPage />} />
            <Route path="room/:code" element={<RoomPage />} />
            <Route path="game/:gameId" element={<GamePage />} />
            <Route path="history" element={<HistoryPage />} />
            <Route path="history/:gameId" element={<ReplayPage />} />
          </Route>
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

export default App
