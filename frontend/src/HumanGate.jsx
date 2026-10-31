import { useEffect, useRef, useState } from 'react'
import { API_BASE, readPass, savePass } from './api.js'

const TURNSTILE_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'

function loadTurnstile() {
  if (window.turnstile) return Promise.resolve(window.turnstile)
  return new Promise((resolve, reject) => {
    const existing = document.querySelector(`script[src="${TURNSTILE_SRC}"]`)
    const s = existing || document.createElement('script')
    s.addEventListener('load', () => resolve(window.turnstile))
    s.addEventListener('error', () => reject(new Error('Turnstile failed to load')))
    if (!existing) {
      s.src = TURNSTILE_SRC
      s.async = true
      document.head.appendChild(s)
    }
  })
}

/**
 * Shows a human check before the app when the server requires one: a typed code (default) or
 * Cloudflare Turnstile, as the server's /session/config says.
 * The server confirms the check and returns a session pass that every API call then carries.
 */
export default function HumanGate({ children }) {
  // 'checking' → asking the server; 'open' → app; 'verify' → show the check.
  const [state, setState] = useState('checking')
  const [mode, setMode] = useState('code')
  const [siteKey, setSiteKey] = useState('')
  const [error, setError] = useState(null)
  const widgetEl = useRef(null)
  const widgetId = useRef(null)

  useEffect(() => {
    fetch(`${API_BASE}/session/config`)
      .then((r) => r.json())
      .then((cfg) => {
        if (!cfg.required) { setState('open'); return }
        setMode(cfg.mode || 'code')
        setSiteKey(cfg.siteKey)
        setState(readPass() ? 'open' : 'verify')
      })
      // Server unreachable: let the app load; its own requests will show the error.
      .catch(() => setState('open'))
    // A pass that expires mid-visit sends the visitor back to the check.
    const reverify = () => setState('verify')
    window.addEventListener('floodmap:verify', reverify)
    return () => window.removeEventListener('floodmap:verify', reverify)
  }, [])

  useEffect(() => {
    if (state !== 'verify' || mode !== 'turnstile' || !siteKey) return
    let cancelled = false
    loadTurnstile()
      .then((turnstile) => {
        if (cancelled || !widgetEl.current) return
        widgetId.current = turnstile.render(widgetEl.current, {
          sitekey: siteKey,
          theme: 'light',
          callback: async (token) => {
            setError(null)
            try {
              const res = await fetch(`${API_BASE}/session`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ token }),
              })
              const body = await res.json()
              if (!res.ok || !body.pass) throw new Error(body.error || 'Verification failed')
              savePass(body.pass, body.expiresAt)
              setState('open')
            } catch (e) {
              // No automatic retry: a "Try again" button, so a failing or rate-limited server isn't hammered.
              setError(e.message || 'Verification failed — please try again.')
            }
          },
          'error-callback': () => setError('The check couldn’t load. Please refresh the page.'),
          'expired-callback': () => turnstile.reset(widgetId.current),
        })
      })
      .catch(() => setError('The security check couldn’t load. Check your connection and refresh.'))
    return () => {
      cancelled = true
      if (widgetId.current != null && window.turnstile) window.turnstile.remove(widgetId.current)
      widgetId.current = null
    }
  }, [state, mode, siteKey])

  if (state === 'open') return children
  return (
    <main className="gate" aria-labelledby="gate-title">
      <div className="gate-card">
        <p className="gate-eyebrow">OmniGuard</p>
        <h1 id="gate-title">What risks does your address face?</h1>
        {state === 'checking' ? (
          <p className="gate-text" role="status">Loading…</p>
        ) : (
          <>
            {mode === 'code' ? (
              <CodeCheck onPass={() => setState('open')} />
            ) : (
              <>
                <p className="gate-text">A quick check keeps automated traffic off this free tool. It usually finishes on its own.</p>
                <div ref={widgetEl} className="gate-widget" />
              </>
            )}
            {error && (
              <div className="gate-error" role="alert">
                <p>{error}</p>
                <button
                  type="button"
                  onClick={() => { setError(null); if (window.turnstile && widgetId.current != null) window.turnstile.reset(widgetId.current) }}
                >
                  Try again
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </main>
  )
}

/** Typed-code captcha: the server draws the code and checks the answer; each code works once. */
function CodeCheck({ onPass }) {
  const [captcha, setCaptcha] = useState(null)
  const [answer, setAnswer] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState(null)
  const inputRef = useRef(null)

  async function newCode(keepMessage = false) {
    if (!keepMessage) setMessage(null)
    setAnswer('')
    try {
      const res = await fetch(`${API_BASE}/captcha`)
      const body = await res.json()
      if (!res.ok) throw new Error(body.error || 'Couldn’t load a code.')
      setCaptcha(body)
    } catch (e) {
      setCaptcha(null)
      setMessage(e.message || 'Couldn’t load a code. Please try again.')
    }
    inputRef.current?.focus()
  }

  useEffect(() => { newCode() }, [])

  async function submit(e) {
    e.preventDefault()
    if (!captcha || !answer.trim() || busy) return
    setBusy(true)
    try {
      const res = await fetch(`${API_BASE}/session`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ captchaId: captcha.id, answer }),
      })
      const body = await res.json()
      if (res.ok && body.pass) {
        savePass(body.pass, body.expiresAt)
        onPass()
        return
      }
      // Every attempt uses up the code, so a miss always gets a fresh one.
      setMessage(body.error || 'That code didn’t match — here’s a new one.')
      await newCode(true)
    } catch {
      setMessage('Couldn’t reach the server. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="code-check" onSubmit={submit}>
      <p className="gate-text">Type the characters you see to keep automated traffic off this free tool.</p>
      <div className="code-image">
        {captcha ? <img src={captcha.image} alt="Security code: distorted letters and numbers" width="260" height="90" /> : <span>Loading…</span>}
        <button type="button" className="code-refresh" onClick={() => newCode()} aria-label="Show a different code" title="Show a different code">↻</button>
      </div>
      <label htmlFor="captcha-answer" className="code-label">Characters in the image</label>
      <div className="code-row">
        <input
          id="captcha-answer"
          ref={inputRef}
          value={answer}
          onChange={(e) => setAnswer(e.target.value.toUpperCase())}
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck="false"
          maxLength={10}
          aria-describedby={message ? 'captcha-msg' : undefined}
        />
        <button type="submit" disabled={busy || !answer.trim()}>{busy ? '…' : 'Continue'}</button>
      </div>
      {message && <p id="captcha-msg" className="gate-error" role="alert">{message}</p>}
      <p className="code-hint">Not case-sensitive. Can’t read it? Press ↻ for a new one.</p>
    </form>
  )
}
