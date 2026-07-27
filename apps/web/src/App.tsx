import { BrowserRouter, Outlet, Route, Routes } from 'react-router'
import { AppShell } from './components/AppShell'
import { SessionGate } from './features/session/SessionGate'
import { GamePage } from './routes/GamePage'
import { HomePage } from './routes/HomePage'
import { HistoryPage } from './routes/HistoryPage'
import { MatchPage } from './routes/MatchPage'
import { ReplayPage } from './routes/ReplayPage'
import { RoomPage } from './routes/RoomPage'

function App() {
  return (
    <BrowserRouter>
      <Routes>
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
