import { useState } from 'react'
import { ALERT_AT, categoryFor, fmtMi, kmToMi } from './hazards.js'

const SEVERITY_CLASS = { Low: 'low', Moderate: 'moderate', High: 'high', Severe: 'severe' }

// Every risk color travels with its word, so color is never the only signal.
function RiskChip({ score }) {
  if (score == null) return null
  const word = categoryFor(score)
  return <span className={`risk-chip ${SEVERITY_CLASS[word]}`}>{word} · {Math.round(score)}</span>
}

export function CompoundBanner({ result, loading, searched }) {
  const [open, setOpen] = useState(false)
  return (
    <section className="banner" aria-label="Compound risk">
      <div className={`banner-score ${result ? SEVERITY_CLASS[result.word] : ''}`}>
        <b>{result ? result.score : '—'}</b>
        <span>{result ? result.word : 'No score'}</span>
      </div>
      <div className="banner-text" aria-live="polite">
        <strong>Compound risk{loading ? ' · updating…' : ''}</strong>
        {result ? (
          <span className="banner-alerts">
            {result.alerts.length > 0
              ? <>Active alerts: {result.alerts.map((a) => `${a.label} (${categoryFor(a.score)})`).join(' · ')}</>
              : `No active alerts · ${result.known} hazards checked`}
          </span>
        ) : (
          <span className="banner-alerts">{searched ? 'Checking this location…' : 'Search an address to combine its flood, air, fire and water risks.'}</span>
        )}
      </div>
      <div className="banner-help">
        <button type="button" className="help-btn" aria-expanded={open} aria-controls="score-help" onClick={() => setOpen(!open)}>
          How is this scored?
        </button>
        {open && (
          <div id="score-help" className="help-pop">
            <b>Each hazard gets a 0–100 score</b>
            <ul>
              <li><b>Flood:</b> the AI score for the selected rain (the terrain score if the AI is unavailable).</li>
              <li><b>Air:</b> AQI ÷ 2, capped at 100.</li>
              <li><b>Fire:</b> 80 if an active fire is within 25 km, 50 if within 50 km, 30 if a burn scar from the last 5 years is within 10 km, otherwise 5.</li>
              <li><b>Water:</b> 70 for an alert, 30 for a watch, 5 for good.</li>
            </ul>
            <p><b>Compound</b> = 0.6 × the highest + 0.4 × the average. Hazards with no data are left out. Any hazard at {ALERT_AT} or more is listed as an alert.</p>
            <p>This is a screening guide, not an official rating.</p>
          </div>
        )}
      </div>
    </section>
  )
}

function Card({ icon, title, score, children }) {
  return (
    <article className="hazard-card">
      <header>
        <h3><span aria-hidden="true">{icon}</span> {title}</h3>
        <RiskChip score={score} />
      </header>
      {children}
    </article>
  )
}

const shortDay = (date) => new Date(`${date}T12:00:00`).toLocaleDateString('en-US', { weekday: 'short' })
const longDay = (date) => new Date(`${date}T12:00:00`).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
const aqiWord = (aqi) => (aqi <= 50 ? 'Good' : aqi <= 100 ? 'Moderate' : aqi <= 150 ? 'Unhealthy for sensitive groups' : aqi <= 200 ? 'Unhealthy' : aqi <= 300 ? 'Very unhealthy' : 'Hazardous')

// Daily maximum AQI as thin bars; the readout below names the hovered or focused day.
function AirChart({ history }) {
  const [active, setActive] = useState(null)
  if (!history?.length) return null
  const top = Math.max(100, ...history.map((d) => d.maxAqi))
  const peak = history.reduce((a, b) => (b.maxAqi > a.maxAqi ? b : a))
  const shown = active ?? peak
  return (
    <div className="air-chart">
      <div
        className="air-bars"
        role="img"
        aria-label={`Daily highest AQI, last ${history.length} days: ${history.map((d) => `${longDay(d.date)} ${d.maxAqi} ${aqiWord(d.maxAqi)}`).join(', ')}`}
        onMouseLeave={() => setActive(null)}
      >
        {history.map((d) => (
          <div key={d.date} className="air-col" onMouseEnter={() => setActive(d)}>
            <span className="air-bar" style={{ height: `${Math.max(4, (d.maxAqi / top) * 100)}%` }} />
          </div>
        ))}
      </div>
      <div className="air-days" aria-hidden="true">{history.map((d) => <span key={d.date}>{shortDay(d.date)[0]}</span>)}</div>
      <small className="air-readout">{active ? '' : 'Week’s high: '}{longDay(shown.date)}, AQI {shown.maxAqi} ({aqiWord(shown.maxAqi)})</small>
    </div>
  )
}

function Unavailable({ what }) {
  return <p className="card-muted">{what} isn’t available right now.</p>
}

function Loading() {
  return <p className="card-muted">Loading…</p>
}

function FacilityList({ list, onShow }) {
  return (
    <ul className="facility-list">
      {list.map((f) => (
        <li key={`${f.name}${f.lat}`}>
          <button type="button" className="link-btn" onClick={() => onShow(f)}>{f.name}</button>
          <small>{fmtMi(f.distanceKm)} · {f.detail}</small>
        </li>
      ))}
    </ul>
  )
}

export function HazardCards({ selected, hazards, tap, scores, onOpenTap, onShowFacility }) {
  if (!selected) {
    return (
      <section className="cards" aria-label="Hazards at this address">
        {[['🌊', 'Flood'], ['🌫', 'Air Quality'], ['🔥', 'Fire Risk'], ['💧', 'Water Quality'], ['🏫', 'Shelters & 🏥 Hospitals']].map(([icon, title]) => (
          <Card key={title} icon={icon} title={title}><p className="card-muted">Search an address</p></Card>
        ))}
      </section>
    )
  }

  const f = selected.features
  const h = hazards.data
  const zone = h?.floodZone
  const fire = h?.fire
  const air = h?.air
  const rainWord = f.scenario === 'storm' ? `${Number(f.rainInches.toFixed(2))} in storm` : '1 in of rain'
  const places = [...(h?.shelters || []), ...(h?.hospitals || [])]

  return (
    <section className="cards" aria-label="Hazards at this address">
      <Card icon="🌊" title="Flood" score={scores.flood}>
        <p className="card-lead">In a {rainWord}</p>
        {hazards.loading ? <Loading /> : zone ? (
          <>
            <p className="card-stat">
              <b>{zone.zone ? `FEMA zone ${zone.zone}` : 'No FEMA map'}</b>
              {zone.zone && <span className={zone.highRisk ? 'word-bad' : ''}>{zone.highRisk ? 'High-risk area' : 'Not high-risk'}</span>}
            </p>
            <p className="card-note">{zone.meaning}</p>
          </>
        ) : <Unavailable what="FEMA flood-zone data" />}
      </Card>

      <Card icon="🌫" title="Air Quality" score={scores.air}>
        {hazards.loading ? <Loading /> : air ? (
          <>
            <p className="card-stat"><b>AQI {air.aqi}</b><span>{air.category}{air.pm25 != null ? ` · PM2.5 ${air.pm25}` : ''}</span></p>
            <AirChart history={air.history} />
          </>
        ) : <Unavailable what="Air-quality data" />}
      </Card>

      <Card icon="🔥" title="Fire Risk" score={scores.fire}>
        {hazards.loading ? <Loading /> : fire ? (
          <>
            {fire.active.length > 0 ? (
              <>
                <p className="card-stat"><b className="word-bad">{fire.active.length} active {fire.active.length === 1 ? 'fire' : 'fires'}</b></p>
                <ul className="card-list">
                  {fire.active.slice(0, 3).map((a) => <li key={a.name}>{a.name}, {fmtMi(a.distanceKm)} away</li>)}
                </ul>
              </>
            ) : (
              <p className="card-stat"><b>No active fires</b><span>within {Math.round(kmToMi(fire.searchKm))} mi</span></p>
            )}
            {fire.past.length > 0 ? (
              <>
                <p className="card-note">Nearest past fires:</p>
                <ul className="card-list">
                  {fire.past.slice(0, 3).map((p) => <li key={`${p.name}${p.year}`}>{p.name}{p.year ? ` (${p.year})` : ''}, {fmtMi(p.distanceKm)}</li>)}
                </ul>
              </>
            ) : <p className="card-note">No burn scars on record nearby.</p>}
          </>
        ) : <Unavailable what="Fire data" />}
      </Card>

      <Card icon="💧" title="Water Quality" score={scores.water}>
        {tap.loading ? <Loading /> : tap.data ? (
          <>
            <p className="card-note">{tap.data.headline}</p>
            {tap.data.systemName && <p className="card-note"><b>{tap.data.systemName}</b></p>}
            <button type="button" className="link-btn" onClick={onOpenTap}>Open Tap water details</button>
          </>
        ) : <Unavailable what="Tap-water data" />}
      </Card>

      <Card icon="🏫" title="Shelters & 🏥 Hospitals">
        {hazards.loading ? <Loading /> : !h ? <Unavailable what="Shelter and hospital data" /> : places.length === 0 ? (
          <p className="card-note">None found within 19 mi.</p>
        ) : (
          <>
            {h.shelters.length > 0 && <FacilityList list={h.shelters.slice(0, 2)} onShow={onShowFacility} />}
            {h.hospitals.length > 0 && <FacilityList list={h.hospitals.slice(0, 2)} onShow={onShowFacility} />}
            <p className="card-note">
              {h.openShelterCount > 0
                ? <b>{h.openShelterCount} open now within 31 mi.</b>
                : 'No shelters are open now. Listed ones open only in an emergency, so check local news first.'}
            </p>
          </>
        )}
      </Card>
    </section>
  )
}
