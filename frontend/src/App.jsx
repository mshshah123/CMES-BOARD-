import { useEffect, useState } from 'react'
import api from './api/client'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import WipReport from './pages/WipReport'
import ModelTracking from './pages/ModelTracking'
import AccessDenied from './pages/AccessDenied'
import Spinner from './components/Spinner'
import './pages/AccessDenied.css'

// Flow (legacy MES jaisa):
//  1) App khud Windows user detect karke LOGIN landing dikhata hain.
//  2) "Log In" -> DB check (/auth/me). Valid+active -> Dashboard, warna Access Denied.
function App() {
  const [phase, setPhase] = useState('detecting') // detecting | landing | authorized | denied
  const [detected, setDetected] = useState('')
  const [user, setUser] = useState(null)
  const [denied, setDenied] = useState(null)
  const [checking, setChecking] = useState(false)
  const [page, setPage] = useState('dashboard') // 'dashboard' | 'wip' | 'model' - sidebar se switch
  // navKey badhta hain har sidebar click pe (same page dobara click ho tab bhi) -
  // key badalne se React page component ko remount karta hain, jisse uska mount-time
  // fetch effect fir se chal jaata hain aur live DB se fresh data aata hain.
  const [navKey, setNavKey] = useState(0)
  const handleNavigate = (p) => { setPage(p); setNavKey(k => k + 1) }

  // Startup: detected Windows username le aao (DB check ke bina).
  useEffect(() => {
    api.get('/auth/whoami')
      .then(res => setDetected(res.data.display || res.data.wwid || ''))
      .catch(() => setDetected(''))
      .finally(() => setPhase('landing'))
  }, [])

  // Log In -> DB check.
  const doLogin = () => {
    setChecking(true)
    api.get('/auth/me')
      .then(res => { setUser(res.data); setPhase('authorized') })
      .catch(err => {
        setDenied(err.response?.data || { username: detected, message: 'Unable to verify your access. Is the server running?' })
        setPhase('denied')
      })
      .finally(() => setChecking(false))
  }

  // Logout: app-level - wapas login landing pe (Windows auth auto hai, isliye
  // ye session reset karke phir Log In maangta hain).
  const logout = () => { setUser(null); setDenied(null); setPhase('landing') }

  if (phase === 'detecting') return <Spinner full label="Detecting Windows user…" />
  if (phase === 'authorized') {
    const navProps = { user, onLogout: logout, page, onNavigate: handleNavigate }
    if (page === 'wip')   return <WipReport      key={navKey} {...navProps} />
    if (page === 'model') return <ModelTracking  key={navKey} {...navProps} />
    return <Dashboard key={navKey} {...navProps} />
  }
  if (phase === 'denied') return <AccessDenied info={denied} />
  return <Login detected={detected} onLogin={doLogin} loading={checking} />
}

export default App
