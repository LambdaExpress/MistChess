import { BrowserRouter, Route, Routes } from 'react-router'
import { AppShell } from './components/AppShell'
import { SessionGate } from './features/session/SessionGate'
import { GamePage } from './routes/GamePage'
import { HomePage } from './routes/HomePage'
import { MatchPage } from './routes/MatchPage'
import { ReplayPage } from './routes/ReplayPage'
import { RoomPage } from './routes/RoomPage'

function App() {
  return (
    <SessionGate>
      <BrowserRouter>
        <Routes>
          <Route element={<AppShell />}>
            <Route index element={<HomePage />} />
            <Route path="match" element={<MatchPage />} />
            <Route path="room/:code" element={<RoomPage />} />
            <Route path="game/:gameId" element={<GamePage />} />
            <Route path="game/:gameId/replay" element={<ReplayPage />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </SessionGate>
  )
}

export default App
