// Scores behind the compound risk banner. Each hazard gets a plain 0-100 number so one formula can
// combine them; a hazard whose data is missing is left out rather than counted as safe.

// Same scale the AI is told to use.
export function categoryFor(score) {
  return score >= 75 ? 'Severe' : score >= 50 ? 'High' : score >= 25 ? 'Moderate' : 'Low'
}

export const ALERT_AT = 50
const RECENT_BURN_YEARS = 5

export function floodScore(selected) {
  if (!selected) return null
  const ai = selected.ai
  return Math.min(100, ai?.available !== false && ai?.riskScore != null ? ai.riskScore : selected.features.deterministicRisk)
}

export function airScore(air) {
  return air ? Math.min(100, air.aqi / 2) : null
}

export function fireScore(fire) {
  if (!fire) return null
  const nearestActive = Math.min(Infinity, ...fire.active.map((f) => f.distanceKm))
  if (nearestActive <= 25) return 80
  if (nearestActive <= 50) return 50
  const thisYear = new Date().getFullYear()
  const recentScar = fire.past.some((f) => f.distanceKm <= 10 && f.year != null && thisYear - f.year <= RECENT_BURN_YEARS)
  return recentScar ? 30 : 5
}

export function waterScore(tap) {
  return { alert: 70, watch: 30, good: 5 }[tap?.level] ?? null
}

// Compound = 0.6 x the highest + 0.4 x the average, so one serious hazard can't be averaged away.
export function compound(scores) {
  const known = scores.filter((s) => s.score != null)
  if (!known.length) return null
  const values = known.map((s) => s.score)
  const score = Math.round(0.6 * Math.max(...values) + 0.4 * (values.reduce((a, b) => a + b, 0) / values.length))
  return { score, word: categoryFor(score), alerts: known.filter((s) => s.score >= ALERT_AT), known: known.length }
}

export const kmToMi = (km) => km * 0.621371
export const fmtMi = (km) => {
  const mi = kmToMi(km)
  return `${mi < 10 ? mi.toFixed(1) : Math.round(mi)} mi`
}
