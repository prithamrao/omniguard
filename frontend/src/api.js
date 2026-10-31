// In production (Vercel), set VITE_API_URL to your Render backend, e.g. https://you.onrender.com/api
export const API_BASE = import.meta.env.VITE_API_URL || '/api'

// The session pass the API issues after the human check; kept for this browser tab only.
const PASS_KEY = 'floodmap-pass'

export function readPass() {
  try {
    const saved = JSON.parse(sessionStorage.getItem(PASS_KEY) || 'null')
    if (saved && saved.pass && new Date(saved.expiresAt) > new Date()) return saved.pass
  } catch { /* storage blocked: verify again */ }
  return null
}

export function savePass(pass, expiresAt) {
  try { sessionStorage.setItem(PASS_KEY, JSON.stringify({ pass, expiresAt })) } catch { /* ignore */ }
}

export function clearPass() {
  try { sessionStorage.removeItem(PASS_KEY) } catch { /* ignore */ }
}

/**
 * fetch() for our API: sends the session pass, and when the server says verification is needed
 * (pass missing or expired), clears it and asks the gate to show the human check again.
 */
export async function apiFetch(url, options = {}) {
  const pass = readPass()
  const headers = { ...(options.headers || {}), ...(pass ? { 'X-Session': pass } : {}) }
  const res = await fetch(url, { ...options, headers })
  if (res.status === 401) {
    clearPass()
    window.dispatchEvent(new Event('floodmap:verify'))
  }
  return res
}
