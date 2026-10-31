import { useEffect, useRef, useState } from 'react'
import maplibregl from 'maplibre-gl'
import 'maplibre-gl/dist/maplibre-gl.css'

import { API_BASE, apiFetch } from './api.js'

const SCENARIOS = {
  normal: { label: 'Normal rain', mult: 1.0 },
  storm: { label: 'Heavy storm (2.5 in)', mult: 1.6 },
}

// Modern vector basemaps (OpenFreeMap — free, no key) + satellite raster.
const STYLES = {
  streets: { label: 'Streets', style: 'https://tiles.openfreemap.org/styles/liberty' },
  light: { label: 'Light', style: 'https://tiles.openfreemap.org/styles/bright' },
  dark: { label: 'Dark', style: 'https://tiles.openfreemap.org/styles/dark' },
  satellite: {
    label: 'Satellite',
    style: {
      version: 8,
      sources: {
        sat: {
          type: 'raster',
          tiles: ['https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}'],
          tileSize: 256,
          maxzoom: 19,
          attribution: 'Esri, Maxar, Earthstar Geographics',
        },
      },
      layers: [{ id: 'sat', type: 'raster', source: 'sat' }],
    },
  },
}

// 3D terrain: free terrarium DEM tiles (AWS Open Data).
const TERRAIN_SOURCE = {
  type: 'raster-dem',
  tiles: ['https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png'],
  tileSize: 256,
  encoding: 'terrarium',
  maxzoom: 15,
}
const TERRAIN = { source: 'terrain-src', exaggeration: 1.6 }

function riskColorExpr(m) {
  return ['interpolate', ['linear'], ['*', ['get', 'risk'], m],
    0, '#1a9850',
    20, '#1a9850',
    30, '#fee08b',
    50, '#fdae61',
    65, '#f46d43',
    80, '#d73027',
    100, '#7f0000']
}
// Same scale the AI is told to use.
function categoryFor(score) {
  return score >= 75 ? 'Severe' : score >= 50 ? 'High' : score >= 25 ? 'Moderate' : 'Low'
}
const CATEGORY_COLORS = { Low: '#27ae60', Moderate: '#d4a106', High: '#e67e22', Severe: '#c0392b' }

// Each low-spot cell as a small square, so the pond reads as an area, not a dot.
function pondFC(lowSpot, cellDeg) {
  const h = cellDeg / 2
  return {
    type: 'FeatureCollection',
    features: (lowSpot?.cells || []).map((c) => {
      const dLng = h / Math.cos((c.lat * Math.PI) / 180)
      return {
        type: 'Feature',
        geometry: { type: 'Polygon', coordinates: [[[c.lng - dLng, c.lat - h], [c.lng + dLng, c.lat - h], [c.lng + dLng, c.lat + h], [c.lng - dLng, c.lat + h], [c.lng - dLng, c.lat - h]]] },
        properties: {},
      }
    }),
  }
}

const PIN_ICONS = {
  home: '<svg viewBox="0 0 24 24" width="16" height="16" fill="#fff"><path d="M12 3 2 12h3v8h5v-5h4v5h5v-8h3z"/></svg>',
  lowspot: '<svg viewBox="0 0 24 24" width="15" height="15" fill="#fff"><path d="M12 2s-7 8.1-7 13a7 7 0 0 0 14 0c0-4.9-7-13-7-13z"/></svg>',
  inflow: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v16M5 13l7 7 7-7"/></svg>',
  drain: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="#fff" stroke-width="2.2"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M8 4v16M12 4v16M16 4v16"/></svg>',
  street: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round"><path d="M6 3 4 21M18 3l2 18M12 4v3M12 11v3M12 18v2"/></svg>',
  rear: '<svg viewBox="0 0 24 24" width="15" height="15" fill="#fff"><path d="M12 2s-7 8.1-7 13a7 7 0 0 0 14 0c0-4.9-7-13-7-13z"/></svg>',
}

function escapeHtml(t) {
  return String(t).replace(/[&<>"]/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[ch])
}

const REPORT_ICONS = {
  pond: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round"><path d="M2 9c2.5 0 2.5-2 5-2s2.5 2 5 2 2.5-2 5-2 2.5 2 5 2M2 15c2.5 0 2.5-2 5-2s2.5 2 5 2 2.5-2 5-2 2.5 2 5 2"/></svg>',
  drain: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#fff" stroke-width="2.2"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M8 4v16M12 4v16M16 4v16"/></svg>',
}

// A resident-reported pond or storm drain; its popup can remove the report.
function reportMarker(m, r, onRemove) {
  const el = document.createElement('div')
  el.className = `report-pin report-${r.kind}`
  el.innerHTML = REPORT_ICONS[r.kind] || REPORT_ICONS.pond
  const box = document.createElement('div')
  box.className = 'report-popup'
  box.innerHTML = `<b>${r.kind === 'drain' ? 'Storm drain' : 'Pond'} (reported)</b><span>Used in the runoff analysis for nearby homes.</span>`
  const btn = document.createElement('button')
  btn.type = 'button'
  btn.textContent = 'Remove report'
  btn.onclick = () => onRemove(r.id)
  box.appendChild(btn)
  const popup = new maplibregl.Popup({ closeButton: true, offset: 14, maxWidth: '240px' }).setDOMContent(box)
  const marker = new maplibregl.Marker({ element: el }).setLngLat([r.lng, r.lat]).setPopup(popup).addTo(m)
  // Stop the click reaching the map (which would assess this spot) and open the popup ourselves.
  el.addEventListener('click', (e) => { e.stopPropagation(); marker.togglePopup() })
  return marker
}

// A map pin for one issue; hovering (or tapping) it explains what the problem is.
function hotspotMarker(m, h, flagged) {
  const el = document.createElement('div')
  el.className = `pin pin-${h.kind}${flagged ? ' pin-flagged' : ''}`
  // MapLibre owns the marker element's transform, so the rotated teardrop lives on an inner element.
  el.innerHTML = `<span class="pin-body"><span class="pin-icon">${PIN_ICONS[h.kind] || PIN_ICONS.home}</span></span>`
  const popup = new maplibregl.Popup({ closeButton: false, closeOnClick: false, offset: [0, -32], maxWidth: '280px', className: 'hotspot-popup' })
    .setHTML(`<b>${escapeHtml(h.title)}</b><ul>${h.details.map((d) => `<li>${escapeHtml(d)}</li>`).join('')}</ul>`)
  const marker = new maplibregl.Marker({ element: el, anchor: 'bottom' }).setLngLat([h.lng, h.lat]).setPopup(popup).addTo(m)
  el.addEventListener('mouseenter', () => { if (!popup.isOpen()) marker.togglePopup() })
  el.addEventListener('mouseleave', () => { if (popup.isOpen()) marker.togglePopup() })
  el.addEventListener('click', (e) => e.stopPropagation())
  return marker
}

function categoryColorExpr() {
  return ['match', ['get', 'cat'],
    'Low', '#27ae60', 'Moderate', '#f1c40f', 'High', '#e67e22', 'Severe', '#c0392b', '#555']
}
function pointsFC(points) {
  return {
    type: 'FeatureCollection',
    features: points.map((p) => ({
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [p.lng, p.lat] },
      properties: {
        risk: p.risk,
        elevation: p.elevation,
        slope: p.slope,
        depression: p.depression,
        flowAcc: p.flowAcc,
      },
    })),
  }
}
function pathFC(path) {
  return {
    type: 'FeatureCollection',
    features: [{ type: 'Feature', geometry: { type: 'LineString', coordinates: path.map((p) => [p.lng, p.lat]) }, properties: {} }],
  }
}
function historyFC(history) {
  return {
    type: 'FeatureCollection',
    features: history.map((h) => ({
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [h.lng, h.lat] },
      properties: { cat: h.ai?.category || '' },
    })),
  }
}

export default function App() {
  const mapEl = useRef(null)
  const map = useRef(null)
  const data = useRef({ points: [], flowPath: [], streetIndex: null, surface: null, history: [], lowSpot: null, hotspots: [], parcel: null, reports: [], cellDeg: 0.00012 })
  const markers = useRef([])
  const reportMarkers = useRef([])
  const scenarioRef = useRef('normal')
  const lastReq = useRef(null)          // the property on screen, so a scenario switch or new report re-assesses it
  const reportModeRef = useRef(null)    // 'pond' | 'drain' while placing a report
  const pulseRef = useRef(null)
  const mult = useRef(1)
  const selectRef = useRef(() => {})
  const terrain3dRef = useRef(false)
  const riskShown = useRef(false)
  const searchedRef = useRef(false)
  const selectedLoc = useRef(null)
  const debounceRef = useRef(null)
  const suggestSeq = useRef(0)      // bumps on every keystroke and submit; stale autocomplete replies are dropped
  const skipFirst = useRef({ basemap: true, terrain: true })

  const [basemap, setBasemap] = useState('satellite')
  const [terrain3d, setTerrain3d] = useState(false)
  const [scenario, setScenario] = useState('normal')
  const [selected, setSelected] = useState(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(null)
  const [query, setQuery] = useState('')
  const [searching, setSearching] = useState(false)
  const [suggestions, setSuggestions] = useState([])
  const [showSuggestions, setShowSuggestions] = useState(false)
  const [activeIndex, setActiveIndex] = useState(-1)
  const [reportMode, setReportMode] = useState(null)
  const [tab, setTab] = useState('flood')
  const [tap, setTap] = useState({ key: null, data: null, loading: false })

  function applyOverlays() {
    const m = map.current
    if (!m) return
    ;['risk-layer', 'parcel-casing', 'parcel-line', 'pond-fill', 'inflow-layer', 'rear-layer', 'flow-layer', 'sewer-layer', 'history-layer'].forEach((id) => { if (m.getLayer(id)) m.removeLayer(id) })
    ;['risk-src', 'parcel-src', 'pond-src', 'inflow-src', 'rear-src', 'flow-src', 'sewer-src', 'history-src'].forEach((id) => { if (m.getSource(id)) m.removeSource(id) })

    const d = data.current
    if (d.points.length && riskShown.current) {
      m.addSource('risk-src', { type: 'geojson', data: pointsFC(d.points) })
      m.addLayer({
        id: 'risk-layer', type: 'circle', source: 'risk-src',
        paint: {
          'circle-radius': 4,
          'circle-color': riskColorExpr(mult.current),
          'circle-opacity': 0.8,
          'circle-stroke-width': 0,
          'circle-pitch-alignment': 'map',
        },
      })
    }
    if (d.parcel?.outline?.length > 2) {
      const ring = d.parcel.outline.map((c) => [c.lng, c.lat])
      m.addSource('parcel-src', { type: 'geojson', data: { type: 'Feature', geometry: { type: 'LineString', coordinates: [...ring, ring[0]] }, properties: {} } })
      // Dark casing under a bright line keeps the lot readable on both pale yards and dark roofs.
      m.addLayer({ id: 'parcel-casing', type: 'line', source: 'parcel-src', paint: { 'line-color': '#1b1b1b', 'line-width': 5, 'line-opacity': 0.55 } })
      m.addLayer({ id: 'parcel-line', type: 'line', source: 'parcel-src', paint: { 'line-color': '#ffd400', 'line-width': 2.5 } })
    }
    clearInterval(pulseRef.current)
    if (d.lowSpot?.cells?.length) {
      const overflow = d.lowSpot.overflows
      m.addSource('pond-src', { type: 'geojson', data: pondFC(d.lowSpot, d.cellDeg) })
      // No per-cell outline and no antialiasing, so adjacent cells merge into one smooth area.
      m.addLayer({
        id: 'pond-fill', type: 'fill', source: 'pond-src',
        paint: { 'fill-color': overflow ? '#ff3b30' : '#1e90ff', 'fill-opacity': 0.4, 'fill-antialias': false, 'fill-opacity-transition': { duration: 700 } },
      })
      // An overflowing low spot pulses, so the problem is impossible to miss.
      if (overflow) {
        let hi = false
        pulseRef.current = setInterval(() => {
          hi = !hi
          if (m.getLayer('pond-fill')) m.setPaintProperty('pond-fill', 'fill-opacity', hi ? 0.65 : 0.25)
        }, 750)
      }
    }
    const inflow = d.hotspots.find((h) => h.kind === 'inflow')
    if (inflow?.path?.length > 1) {
      m.addSource('inflow-src', { type: 'geojson', data: pathFC(inflow.path) })
      m.addLayer({
        id: 'inflow-layer', type: 'line', source: 'inflow-src',
        layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': '#ff8c1a', 'line-width': 4, 'line-opacity': 0.95, 'line-dasharray': [2, 1.2] },
      })
    }
    const backPath = d.surface?.backPath?.length > 1
      ? (d.lowSpot?.viaRearYard ? [...d.surface.backPath, { lat: d.lowSpot.lat, lng: d.lowSpot.lng }] : d.surface.backPath)
      : d.lowSpot?.viaRearYard && d.parcel ? [{ lat: d.parcel.lat, lng: d.parcel.lng }, { lat: d.lowSpot.lat, lng: d.lowSpot.lng }] : null
    if (backPath) {
      m.addSource('rear-src', { type: 'geojson', data: pathFC(backPath) })
      m.addLayer({
        id: 'rear-layer', type: 'line', source: 'rear-src',
        layout: { 'line-cap': 'round' },
        paint: { 'line-color': '#1e90ff', 'line-width': 3, 'line-opacity': 0.9, 'line-dasharray': [1, 1.5] },
      })
    }
    const cut = d.streetIndex != null ? d.streetIndex + 1 : d.flowPath.length
    const overland = d.flowPath.slice(0, cut)
    const sewer = d.flowPath.slice(Math.max(0, cut - 1))
    if (sewer.length > 1) {
      m.addSource('sewer-src', { type: 'geojson', data: pathFC(sewer) })
      m.addLayer({
        id: 'sewer-layer', type: 'line', source: 'sewer-src',
        layout: { 'line-cap': 'round' },
        paint: { 'line-color': '#9be7ff', 'line-width': 2.5, 'line-opacity': 0.8, 'line-dasharray': [0.5, 2] },
      })
    }
    if (overland.length > 1) {
      m.addSource('flow-src', { type: 'geojson', data: pathFC(overland) })
      m.addLayer({
        id: 'flow-layer', type: 'line', source: 'flow-src',
        layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': '#00d5ff', 'line-width': 4, 'line-opacity': 0.95, 'line-dasharray': [2, 1.2] },
      })
    }
    if (d.history.length) {
      m.addSource('history-src', { type: 'geojson', data: historyFC(d.history) })
      m.addLayer({
        id: 'history-layer', type: 'circle', source: 'history-src',
        paint: { 'circle-radius': 6, 'circle-color': categoryColorExpr(), 'circle-opacity': 0.9, 'circle-stroke-color': '#333', 'circle-stroke-width': 1, 'circle-pitch-alignment': 'map' },
      })
    }
    updateMarkers()
  }

  // DOM markers sit above every layer and survive basemap switches.
  function updateMarkers() {
    const m = map.current
    if (!m) return
    reportMarkers.current.forEach((mk) => mk.remove())
    reportMarkers.current = data.current.reports.map((r) => reportMarker(m, r, removeReport))
    markers.current.forEach((mk) => mk.remove())
    markers.current = []
    const spots = data.current.hotspots
    if (spots.length) {
      const flagged = (h) => h.kind !== 'home' || !h.details[0]?.startsWith('No significant')
      // Draw the house last so it sits on top when pins overlap.
      ;[...spots].sort((a, b) => (a.kind === 'home') - (b.kind === 'home'))
        .forEach((h) => markers.current.push(hotspotMarker(m, h, flagged(h))))
    } else if (selectedLoc.current) {
      // While the assessment loads, just show where the user clicked.
      markers.current.push(hotspotMarker(m, { kind: 'home', ...selectedLoc.current, title: 'Analyzing…', details: ['Checking terrain, drainage and paving here.'] }, false))
    }
  }

  function ensureTerrain() {
    const m = map.current
    if (!m) return
    if (terrain3dRef.current) {
      if (!m.getSource('terrain-src')) m.addSource('terrain-src', TERRAIN_SOURCE)
      m.setTerrain(TERRAIN)
    } else if (m.getTerrain()) {
      m.setTerrain(null)
    }
  }

  async function handleSelect(lat, lng, houseNumber = null, street = null) {
    lastReq.current = { lat, lng, houseNumber, street }
    setLoading(true)
    setError(null)
    riskShown.current = true
    selectedLoc.current = { lat, lng }
    data.current.flowPath = []
    data.current.streetIndex = null
    data.current.surface = null
    data.current.lowSpot = null
    data.current.hotspots = []
    data.current.parcel = null
    applyOverlays()
    try {
      const assessRes = await apiFetch(`${API_BASE}/assess`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ lat, lng, houseNumber, street, scenario: scenarioRef.current }),
      }).then((r) => r.json())
      if (assessRes.error) {
        setError(assessRes.error)
      } else {
        setSelected({ ...assessRes, searched: !!houseNumber })
        data.current.flowPath = assessRes.flowPath || []
        data.current.streetIndex = assessRes.features?.streetPathIndex ?? null
        data.current.surface = assessRes.features?.surface || null
        data.current.lowSpot = assessRes.features?.lowSpot || null
        data.current.hotspots = assessRes.features?.hotspots || []
        data.current.parcel = assessRes.features?.parcel || null
        loadReports(lat, lng)
        data.current.history = [assessRes, ...data.current.history.filter((x) => !x.id || x.id !== assessRes.id)]
        // Put the marker on the exact house the backend assessed (matching house number), if it found one.
        if (assessRes.features?.snappedLat != null && assessRes.features?.snappedLng != null) {
          selectedLoc.current = { lat: assessRes.features.snappedLat, lng: assessRes.features.snappedLng }
        }
      }
      applyOverlays()
      return assessRes
    } catch (e) {
      setError('Assessment failed — is the API running?')
      console.error(e)
    } finally {
      setLoading(false)
    }
  }
  // Map clicks place a report while report mode is on; otherwise they assess the clicked spot.
  selectRef.current = (lat, lng) => (reportModeRef.current ? placeReport(lat, lng) : handleSelect(lat, lng))

  async function loadReports(lat, lng) {
    try {
      const list = await apiFetch(`${API_BASE}/reports/near?lat=${lat}&lng=${lng}&radius=2500`).then((r) => r.json())
      data.current.reports = Array.isArray(list) ? list : []
      updateMarkers()
    } catch { /* reports are optional */ }
  }

  function reassess() {
    const q = lastReq.current
    if (q) handleSelect(q.lat, q.lng, q.houseNumber, q.street)
  }

  function setReporting(kind) {
    reportModeRef.current = kind
    setReportMode(kind)
    const m = map.current
    if (m) m.getCanvas().style.cursor = kind ? 'crosshair' : ''
  }

  async function placeReport(lat, lng) {
    const kind = reportModeRef.current
    setReporting(null)
    try {
      await apiFetch(`${API_BASE}/reports`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ lat, lng, kind }) })
    } catch { setError('Could not save the report.'); return }
    if (lastReq.current) reassess()
    else loadReports(lat, lng)
  }

  async function removeReport(id) {
    try { await apiFetch(`${API_BASE}/reports/${id}`, { method: 'DELETE' }) } catch { /* ignore */ }
    data.current.reports = data.current.reports.filter((r) => r.id !== id)
    if (lastReq.current) reassess()
    else updateMarkers()
  }

  async function loadArea(lat, lng, zoom) {
    const areaData = await apiFetch(`${API_BASE}/map/area?lat=${lat}&lng=${lng}&stride=4`).then((r) => r.json())
    if (areaData.error) { setError(areaData.error); return false }
    data.current.points = areaData.points || []
    if (areaData.pixelLat) data.current.cellDeg = Math.abs(areaData.pixelLat)
    riskShown.current = true
    applyOverlays()
    const m = map.current
    if (m) m.jumpTo({ center: [lng, lat], zoom })
    return true
  }

  async function loadAndAssess(lat, lng, houseNumber, street) {
    const ok = await loadArea(lat, lng, 17)
    if (!ok) return false
    searchedRef.current = true
    try { localStorage.setItem('lastSearch', JSON.stringify({ lat, lng })) } catch { /* ignore */ }
    const res = await handleSelect(lat, lng, houseNumber, street)
    // Frame the house together with where its water ends up, without zooming out past street level.
    const m = map.current
    const pts = (res?.features?.hotspots || []).map((h) => [h.lng, h.lat])
    if (m && pts.length > 1) {
      const b = pts.reduce((bb, p) => bb.extend(p), new maplibregl.LngLatBounds(pts[0], pts[0]))
      const panel = window.innerWidth > 700 ? 360 : 0
      m.fitBounds(b, { padding: { top: 80, bottom: 80, left: panel + 60, right: 80 }, maxZoom: 18, minZoom: 15, duration: 600 })
    }
    return true
  }

  async function runSearch(qText, fallbackLat, fallbackLng) {
    setSearching(true)
    setError(null)
    try {
      let target = null
      try {
        const d = await apiFetch(`${API_BASE}/geocode?address=${encodeURIComponent(qText)}`).then((r) => r.json())
        if (!d.error) target = { lat: d.lat, lng: d.lng, houseNumber: d.houseNumber, label: d.matchedAddress }
      } catch { /* geocode failed */ }
      if (!target && fallbackLat != null && fallbackLng != null) target = { lat: fallbackLat, lng: fallbackLng }
      if (!target) { setError('Address not found — try a more specific one.'); return }

      await loadAndAssess(target.lat, target.lng, target.houseNumber ?? leadingHouseNumber(qText), streetOf(target.label || qText))
    } catch (e) {
      setError('Address search failed.')
      console.error(e)
    } finally {
      setSearching(false)
    }
  }

  function handleSearch(e) {
    e.preventDefault()
    const q = query.trim()
    if (!q) return
    clearTimeout(debounceRef.current)   // a late autocomplete reply must not reopen the list
    suggestSeq.current++
    setSuggestions([])
    setShowSuggestions(false)
    runSearch(q, null, null)
  }

  function handleQueryChange(e) {
    const v = e.target.value
    setQuery(v)
    setActiveIndex(-1)
    if (v.trim().length < 3) { setSuggestions([]); setShowSuggestions(false); return }
    clearTimeout(debounceRef.current)
    const seq = ++suggestSeq.current
    debounceRef.current = setTimeout(async () => {
      try {
        const m = map.current
        const c = m ? m.getCenter() : null
        const bias = c ? `&lat=${c.lat}&lng=${c.lng}` : ''
        const list = await apiFetch(`${API_BASE}/suggest?q=${encodeURIComponent(v.trim())}${bias}`).then((r) => r.json())
        if (seq !== suggestSeq.current) return   // the user typed more or already searched
        setSuggestions(list || [])
        setShowSuggestions(true)
      } catch { setSuggestions([]); setShowSuggestions(false) }
    }, 300)
  }

  function handleKeyDown(e) {
    if (e.key === 'ArrowDown' && suggestions.length) {
      e.preventDefault()
      setActiveIndex((i) => (i + 1) % suggestions.length)
      setShowSuggestions(true)
    } else if (e.key === 'ArrowUp' && suggestions.length) {
      e.preventDefault()
      setActiveIndex((i) => (i <= 0 ? suggestions.length - 1 : i - 1))
    } else if (e.key === 'Enter' && activeIndex >= 0) {
      e.preventDefault()
      selectSuggestion(suggestions[activeIndex])
    } else if (e.key === 'Escape') {
      setShowSuggestions(false)
    }
  }

  function selectSuggestion(s) {
    clearTimeout(debounceRef.current)
    suggestSeq.current++
    setQuery(s.label)
    setSuggestions([])
    setShowSuggestions(false)
    // Geocode the full label exactly like a typed search, so picking a suggestion and typing the
    // address always land on the same point; the suggestion's own coordinates are the fallback.
    runSearch(s.label, s.lat, s.lng)
  }

  function clearAll() {
    setQuery('')
    setSuggestions([])
    setShowSuggestions(false)
    setActiveIndex(-1)
    setSelected(null)
    setError(null)
    data.current.points = []
    data.current.flowPath = []
    data.current.history = []
    data.current.lowSpot = null
    data.current.hotspots = []
    data.current.parcel = null
    data.current.reports = []
    lastReq.current = null
    setReporting(null)
    riskShown.current = false
    selectedLoc.current = null
    applyOverlays()
  }

  // Init map once.
  useEffect(() => {
    const m = new maplibregl.Map({
      container: mapEl.current,
      style: STYLES[basemap].style,
      center: [-98.5, 39.8],
      zoom: 4,
    })
    m.addControl(new maplibregl.NavigationControl(), 'top-right')
    m.on('load', () => { applyOverlays(); ensureTerrain() })
    m.on('style.load', () => { applyOverlays(); ensureTerrain() })
    m.on('click', (e) => selectRef.current(e.lngLat.lat, e.lngLat.lng))

    // Hover tooltip on risk dots (and low spots) — shows what the spot means.
    const hoverPopup = new maplibregl.Popup({ closeButton: false, closeOnClick: false, offset: 12 })
    const showHover = (e) => {
      const f = e.features && e.features[0]
      if (!f) return
      const p = f.properties
      const pond = p.depression >= 0.15
      m.getCanvas().style.cursor = 'pointer'
      hoverPopup.setLngLat(e.lngLat).setHTML(
        `<div class="hover-popup">` +
        `<b>${pond ? '💧 Low spot — water stands here' : `Risk ${p.risk.toFixed(0)} · ${categoryFor(p.risk)}`}</b>` +
        `Elevation: ${p.elevation.toFixed(1)} m<br/>` +
        `Slope: ${p.slope.toFixed(1)}%<br/>` +
        (pond ? `Standing water: up to ${p.depression.toFixed(2)} m<br/>` : '') +
        `Uphill drainage: ${p.flowAcc.toFixed(0)} cells<br/>` +
        `<span class="hint">Click to assess this spot</span>` +
        `</div>`,
      ).addTo(m)
    }
    const hideHover = () => { m.getCanvas().style.cursor = ''; hoverPopup.remove() }
    m.on('mousemove', 'risk-layer', showHover)
    m.on('mouseleave', 'risk-layer', hideHover)

    map.current = m
    if (import.meta.env.DEV) window.__map = m   // handy for debugging in the browser console

    // Center on the laptop's location; fall back to the last search, then stay on the US view.
    function goToSaved() {
      if (searchedRef.current) return true
      try {
        const saved = JSON.parse(localStorage.getItem('lastSearch') || 'null')
        if (saved && saved.lat != null && saved.lng != null) {
          m.jumpTo({ center: [saved.lng, saved.lat], zoom: 13 })
          return true
        }
      } catch { /* ignore */ }
      return false
    }
    if (navigator.geolocation) {
      navigator.geolocation.getCurrentPosition(
        (pos) => { if (!searchedRef.current) m.jumpTo({ center: [pos.coords.longitude, pos.coords.latitude], zoom: 13 }) },
        () => { goToSaved() },
        { timeout: 5000 },
      )
    } else {
      goToSaved()
    }

    return () => m.remove()
  }, [])

  // Tap-water quality for the property on screen, fetched once per location.
  useEffect(() => {
    if (!selected) { setTap({ key: null, data: null, loading: false }); return }
    const p = selected.features.parcel
    const lat = p?.lat ?? selected.features.lat
    const lng = p?.lng ?? selected.features.lng
    const key = `${lat.toFixed(5)},${lng.toFixed(5)}`
    if (tap.key === key) return
    setTap({ key, data: null, loading: true })
    apiFetch(`${API_BASE}/tapwater?lat=${lat}&lng=${lng}`)
      .then((r) => r.json())
      .then((data) => setTap((t) => (t.key === key ? { key, data, loading: false } : t)))
      .catch(() => setTap((t) => (t.key === key ? { key, data: { status: 'unavailable', headline: 'Tap-water records couldn’t be loaded right now.', notes: [] }, loading: false } : t)))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected])

  // Scenario toggle -> recolor risk layer and re-assess the property on screen for that rain.
  useEffect(() => {
    const changed = scenarioRef.current !== scenario
    scenarioRef.current = scenario
    if (changed) reassess()
    mult.current = SCENARIOS[scenario].mult
    const m = map.current
    if (m && m.getLayer('risk-layer')) {
      m.setPaintProperty('risk-layer', 'circle-color', riskColorExpr(SCENARIOS[scenario].mult))
    }
  }, [scenario])

  // Basemap switch (skip the initial mount to avoid a redundant setStyle).
  useEffect(() => {
    if (skipFirst.current.basemap) { skipFirst.current.basemap = false; return }
    const m = map.current
    if (m) m.setStyle(STYLES[basemap].style)
  }, [basemap])

  // 3D terrain toggle (skip the initial mount — setTerrain needs a loaded style).
  useEffect(() => {
    terrain3dRef.current = terrain3d
    if (skipFirst.current.terrain) { skipFirst.current.terrain = false; return }
    const m = map.current
    if (!m) return
    ensureTerrain()
    if (terrain3d) m.jumpTo({ pitch: 55, bearing: -20 })
    else m.jumpTo({ pitch: 0 })
  }, [terrain3d])

  return (
    <div className="app">
      <div className="map" ref={mapEl} />

      <div className="panel">
        <div className="panel-header">
        <h1>Stormwater Flood-Risk Map</h1>
        <p className="sub">Search an address or click any point to trace runoff and get an AI risk score.</p>

        <form className="search" onSubmit={handleSearch}>
          <div className="search-row">
            <input
              type="text"
              required
              value={query}
              placeholder="Search an address…"
              onChange={handleQueryChange}
              onKeyDown={handleKeyDown}
              onFocus={() => suggestions.length > 0 && setShowSuggestions(true)}
              onBlur={() => setTimeout(() => setShowSuggestions(false), 150)}
            />
            <button type="submit" disabled={searching}>{searching ? '…' : 'Go'}</button>
            <button type="button" className="clear-btn" onClick={clearAll} title="Clear">✕</button>
          </div>
          {showSuggestions && suggestions.length > 0 && (
            <ul className="suggestions">
              {suggestions.map((s, i) => (
                <li key={i} className={i === activeIndex ? 'active' : ''} onMouseDown={(e) => { e.preventDefault(); selectSuggestion(s) }}>{s.label}</li>
              ))}
            </ul>
          )}
        </form>

        <div className="basemaps">
          {Object.entries(STYLES).map(([k, v]) => (
            <button key={k} className={basemap === k ? 'active' : ''} onClick={() => setBasemap(k)}>
              {v.label}
            </button>
          ))}
        </div>

        <button className={`terrain-toggle ${terrain3d ? 'active' : ''}`} onClick={() => setTerrain3d(!terrain3d)}>
          ⛰ 3D Terrain {terrain3d ? 'On' : 'Off'}
        </button>

        <div className="scenario">
          {Object.entries(SCENARIOS).map(([k, v]) => (
            <button key={k} className={scenario === k ? 'active' : ''} onClick={() => setScenario(k)}>
              {v.label}
            </button>
          ))}
        </div>

        <div className="report-bar">
          {reportMode ? (
            <>
              <span className="report-hint">Click the map where the {reportMode === 'drain' ? 'storm drain' : 'pond'} is…</span>
              <button type="button" onClick={() => setReporting(null)}>Cancel</button>
            </>
          ) : (
            <>
              <span className="report-label">Missing on the map?</span>
              <button type="button" onClick={() => setReporting('pond')} title="Mark a pond the map data doesn't show">+ Pond</button>
              <button type="button" onClick={() => setReporting('drain')} title="Mark a storm drain where runoff leaves">+ Storm drain</button>
            </>
          )}
        </div>

        <div className="legend">
          <span><i style={{ background: '#27ae60' }} /> Low</span>
          <span><i style={{ background: '#f1c40f' }} /> Moderate</span>
          <span><i style={{ background: '#e67e22' }} /> High</span>
          <span><i style={{ background: '#c0392b' }} /> Severe</span>
          <span className="info-icon">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="10" />
              <path d="M12 16v-4" />
              <path d="M12 8h.01" />
            </svg>
            <span className="info-tooltip">
              <b>Flood risk score (0–100)</b>
              <ul>
                <li><i style={{ background: '#1a9850' }} /><span><b>Low (0–24)</b> — generally safe</span></li>
                <li><i style={{ background: '#fee08b' }} /><span><b>Moderate (25–49)</b> — water may pool in heavy rain</span></li>
                <li><i style={{ background: '#fdae61' }} /><span><b>High (50–74)</b> — likely flooding in storms</span></li>
                <li><i style={{ background: '#d73027' }} /><span><b>Severe (75+)</b> — flood-prone, needs action</span></li>
              </ul>
              <span className="hint">Pins mark each issue — hover one to see the problem. Cyan dashes = where your rain runs; orange dashes = uphill water coming in; blue area = where it pools.</span>
            </span>
          </span>
        </div>
        </div>

        <div className="panel-body">
        {error && <div className="error">{error}</div>}
        {loading && <div className="loading"><span className="spinner" /> Analyzing terrain and asking the AI…</div>}

        {selected && !loading && (
          <div className="result">
            <h2>{selected.features.parcel ? lotTitle(selected.features.parcel.address) : 'Property assessment'}</h2>
            {selected.features.parcel ? (
              <div className="where ok">✓ Matched to the official property lot (outlined in yellow)</div>
            ) : selected.searched ? (
              <div className="where warn">📍 Approximate location — official lot data isn't available here. If the pin isn't on your house, click your roof to assess that exact spot.</div>
            ) : (
              <div className="where">📍 Assessing the exact spot you clicked</div>
            )}
            <Tabs
              value={tab}
              onChange={setTab}
              tabs={[
                { id: 'flood', label: '🌊 Flood risk' },
                { id: 'tap', label: '🚰 Tap water', badge: tap.data?.level },
              ]}
            />
            {tab === 'tap' ? (
              <div role="tabpanel" id="panel-tap" aria-labelledby="tab-tap">
                <TapWaterPanel state={tap} />
              </div>
            ) : (
            <div role="tabpanel" id="panel-flood" aria-labelledby="tab-flood">
            {selected.features.scenario === 'storm' && <div className="storm-badge">⛈ Heavy storm scenario · 2.5 in of rain</div>}
            {selected.features.surface ? (
              <div className="survey-badge fresh" title="Lot drainage traced on Google's current aerial surface model (Solar API), not the older public elevation survey.">
                🛰 Aerial surface {selected.features.surface.imageryDate}
                {imageryBeforeHome(selected.features) ? ' (lot graded, before the home was built)' : ''}
                {selected.features.surveyYear ? ` · public survey ${selected.features.surveyYear}` : ''}
              </div>
            ) : selected.features.terrainPredatesHome ? (
              <div className="survey-badge stale" title="The public elevation survey shows the land before this home was built, so runoff follows how new lots are graded instead of the old contours.">
                📅 Terrain survey {selected.features.surveyYear} · Home built {selected.features.parcel?.yearBuilt} — routed by lot grading
              </div>
            ) : selected.features.surveyYear ? (
              <div className="survey-badge">📅 Terrain survey {selected.features.surveyYear}{selected.features.parcel?.yearBuilt ? ` · Home built ${selected.features.parcel.yearBuilt}` : ''}</div>
            ) : null}
            <div className="scores">
              <ScoreCard
                title="Terrain model"
                hint="Calculated from elevation, slope, drainage and paving"
                score={selected.features.deterministicRisk}
              />
              {selected.ai.available !== false ? (
                <ScoreCard title="AI engineer" hint="Independent second opinion from the same measurements" score={selected.ai.riskScore} />
              ) : (
                <div className="score muted"><span>AI engineer</span><b>—</b><small>unavailable</small></div>
              )}
            </div>
            <p className="summary">{selected.ai.summary}</p>
            {selected.features.scenario === 'storm' && selected.ai.stormNote && <p className="storm-note">⛈ {selected.ai.stormNote}</p>}
            {selected.ai.issues && (
              <div className="issues">
                <strong>⚠️ Issues</strong>
                <ul>{selected.ai.issues.split(';').map((x) => x.trim()).filter(Boolean).map((x, i) => <li key={i}>{x}</li>)}</ul>
              </div>
            )}
            {selected.ai.recommendation && (
              <div className="recommendation">
                <strong>💡 What to do</strong> {selected.ai.recommendation}
              </div>
            )}
            <WaterGoes features={selected.features} onShow={(ls) => map.current?.flyTo({ center: [ls.lng, ls.lat], zoom: 18 })} />
            <div className="features">
              <span title="Height above sea level">Elevation {selected.features.elevation.toFixed(1)} m</span>
              <span title="Steepest downhill slope">Slope {selected.features.slope.toFixed(1)}%</span>
              <span title="Land whose runoff flows through this spot">Uphill area {formatArea(selected.features.upstreamAreaM2)}</span>
              <span title={selected.features.parcel?.imperviousSource ? `Estimated from ${selected.features.parcel.imperviousSource}` : 'Roofs, driveways and roads (map-derived estimate)'}>
                Paved {selected.features.imperviousness.toFixed(0)}%{pavedSource(selected.features.parcel?.imperviousSource)}
              </span>
              <span title="Height compared with the surrounding ~200 m">{relLabel(selected.features.relativeLow)}</span>
            </div>
            {selected.id && <div className="saved">✓ Saved · same result every time you check this house</div>}
            </div>
            )}
          </div>
        )}
        </div>
      </div>

    </div>
  )
}

function ScoreCard({ title, hint, score }) {
  const cat = categoryFor(score)
  return (
    <div className="score" title={hint}>
      <span>{title}</span>
      <b>{Math.round(score)}</b>
      <small style={{ color: CATEGORY_COLORS[cat] }}>{cat}</small>
    </div>
  )
}

function WaterGoes({ features, onShow }) {
  const ls = features.lowSpot
  const sd = features.streetDistanceM
  const sf = features.surface
  if (sf) {
    return (
      <div className="water-goes">
        🛰 <b>Where your water goes</b> (traced on the {sf.imageryDate} aerial surface):
        <div className="share-bar" title={`${sf.frontSharePct}% front · ${sf.backSharePct}% back`}>
          <span className="front" style={{ width: `${sf.frontSharePct}%` }}>{sf.frontSharePct >= 15 ? `${sf.frontSharePct}% front` : ''}</span>
          <span className="back" style={{ width: `${sf.backSharePct}%` }}>{sf.backSharePct >= 15 ? `${sf.backSharePct}% back` : ''}</span>
        </div>
        <ul className="water-legs">
          <li><b>{sf.frontSharePct}% of the lot</b> drains to the front → {sd != null ? <>the street {Math.round(sd)} m away, then curbs and storm drains.</> : 'the front of the lot.'}</li>
          <li><b>{sf.backSharePct}% of the lot</b> drains to the back → {ls?.viaRearYard
            ? <>the {ls.reported ? 'pond reported by a resident' : 'pond'} about {Math.round(ls.distanceM)} m behind the house. <button type="button" className="link-btn" onClick={() => onShow(ls)}>Show pond</button></>
            : <>the spot marked with the 💧 pin. If there's a pond or drain there that the map doesn't show, add it with <b>+ Pond</b> / <b>+ Storm drain</b>.</>}</li>
        </ul>
      </div>
    )
  }
  if (features.terrainPredatesHome) {
    return (
      <div className="water-goes">
        🏡 <b>Where your water goes</b> (by lot grading — this home is newer than the {features.surveyYear} elevation survey):
        <ul className="water-legs">
          <li><b>Roof &amp; front yard</b> → {sd != null ? <>the street {Math.round(sd)} m away; curbs and gutters carry it to the storm drains.</> : 'the nearest street and its storm drains.'}</li>
          <li><b>Back yard</b> → {ls?.viaRearYard
            ? <>the {ls.reported ? 'pond reported by a resident' : 'pond'} about {Math.round(ls.distanceM)} m behind the house. <button type="button" className="link-btn" onClick={() => onShow(ls)}>Show pond</button></>
            : <>the rear lot line and swale. If there's a pond or drain back there that the map doesn't show, add it with <b>+ Pond</b> / <b>+ Storm drain</b>.</>}</li>
        </ul>
      </div>
    )
  }
  if (sd != null && (!ls || ls.viaStreet)) {
    return (
      <div className="water-goes">
        🌊 <b>Where your water goes:</b> it reaches the street {Math.round(sd)} m away{sd > 30 ? ' (crossing neighboring yards)' : ''}, where curbs and gutters carry it to the storm drains.
        {ls?.viaStreet && <> Those drains likely empty into a pond about {Math.round(ls.distanceM)} m away (dotted line — pipe route approximate).{' '}
          <button type="button" className="link-btn" onClick={() => onShow(ls)}>Show pond</button></>}
        <div className="caveat">Based on USGS elevation surveys, which can predate new construction — newer grading may differ.</div>
      </div>
    )
  }
  if (!ls) {
    return <div className="water-goes">🌊 <b>Where your water goes:</b> rain from here keeps flowing downhill with no closed low spot within 800 m.</div>
  }
  const where = ls.distanceM === 0 ? 'right here — this property sits in it' : `${ls.distanceM.toFixed(0)} m downhill`
  const rain = features.scenario === 'storm' ? '2.5-inch storm' : '1-inch rain'
  return (
    <div className={`water-goes${ls.overflows ? ' overflow' : ''}`}>
      💧 <b>Where your water goes:</b>{' '}
      {ls.isDrain
        ? <>it reaches a storm drain {where} (reported by a resident).</>
        : ls.isWater
          ? <>it drains into a {ls.reported ? 'pond reported by a resident' : 'pond / stream'} {where}{ls.reported ? '' : ` (about ${formatArea(ls.areaM2)} of water)`}.</>
          : <>it collects in a low spot {where}, up to {ls.depthM.toFixed(2)} m deep (about {formatArea(ls.areaM2)}).</>}{' '}
      {ls.capacityM3 != null && ls.inflowM3 != null && (
        <div className="overflow-line">
          {ls.overflows
            ? <>🚨 In a {rain}, ~{Math.round(ls.inflowM3).toLocaleString()} m³ of runoff arrives but it holds only ~{Math.round(ls.capacityM3).toLocaleString()} m³ — <b>it overflows</b>.</>
            : <>In a {rain}, ~{Math.round(ls.inflowM3).toLocaleString()} m³ of runoff arrives; it holds ~{Math.round(ls.capacityM3).toLocaleString()} m³.</>}
        </div>
      )}
      <button type="button" className="link-btn" onClick={() => onShow(ls)}>Show on map</button>
      <div className="caveat">Based on USGS elevation surveys, which can predate new construction — ponds or grading built since may not appear.</div>
    </div>
  )
}

function formatArea(m2) {
  if (m2 >= 4047) return `${(m2 / 4046.86).toFixed(1)} acres`
  return `${Math.round(m2 * 10.7639).toLocaleString()} sq ft`
}

function relLabel(rel) {
  if (rel >= 0.3) return `${rel.toFixed(1)} m lower than neighbors`
  if (rel <= -0.3) return `${(-rel).toFixed(1)} m higher than neighbors`
  return 'Level with neighbors'
}

function leadingHouseNumber(text) {
  const m = /^\s*(\d+[A-Za-z]?)\b/.exec(text || '')
  return m ? m[1] : null
}

function streetOf(label) {
  // "123 Main Street, Springfield, IL" → "Main Street"
  const m = /^\s*\d+[A-Za-z]?\s+([^,]+)/.exec(label || '')
  return m ? m[1].trim() : null
}

function titleCase(t) {
  return (t || '').toLowerCase().replace(/\b([a-z])/g, (c) => c.toUpperCase())
}

// County records use house number 0 for HOA / common land with no street address.
function lotTitle(address) {
  const t = titleCase(address)
  return /^0\s/.test(t) ? `Common area · ${t.slice(2)}` : t
}

function pavedSource(src) {
  if (!src) return ''
  return src.startsWith('county') ? ' · county records' : ' · land-cover data'
}

// True when the aerial image was taken before the home's build year (the lot is graded but the house isn't there yet).
function imageryBeforeHome(features) {
  const built = features.parcel?.yearBuilt
  const year = parseInt((features.surface?.imageryDate || '').slice(0, 4), 10)
  return built != null && !Number.isNaN(year) && year < built
}

// Accessible tabs: tablist/tab roles, aria-selected, arrow keys move between tabs.
function Tabs({ value, onChange, tabs }) {
  function onKeyDown(e) {
    const i = tabs.findIndex((t) => t.id === value)
    let next = null
    if (e.key === 'ArrowRight') next = tabs[(i + 1) % tabs.length]
    else if (e.key === 'ArrowLeft') next = tabs[(i - 1 + tabs.length) % tabs.length]
    else if (e.key === 'Home') next = tabs[0]
    else if (e.key === 'End') next = tabs[tabs.length - 1]
    if (!next) return
    e.preventDefault()
    onChange(next.id)
    document.getElementById(`tab-${next.id}`)?.focus()
  }
  return (
    <div className="tabs" role="tablist" aria-label="Property details" onKeyDown={onKeyDown}>
      {tabs.map((t) => (
        <button
          key={t.id}
          id={`tab-${t.id}`}
          type="button"
          role="tab"
          aria-selected={value === t.id}
          aria-controls={`panel-${t.id}`}
          tabIndex={value === t.id ? 0 : -1}
          className={value === t.id ? 'active' : ''}
          onClick={() => onChange(t.id)}
        >
          {t.label}
          {t.badge && <span className={`tab-dot ${t.badge}`} aria-label={TAP_LEVEL_WORD[t.badge]} title={TAP_LEVEL_WORD[t.badge]} />}
        </button>
      ))}
    </div>
  )
}

const TAP_LEVEL_WORD = { good: 'No health-based violations', watch: 'Past problems, all resolved', alert: 'Needs attention' }

function TapWaterPanel({ state }) {
  if (state.loading) return <div className="loading"><span className="spinner" /> Checking EPA drinking-water records…</div>
  const d = state.data
  if (!d) return null
  const icon = { good: '✅', watch: 'ℹ️', alert: '⚠️' }[d.level] || '🚰'
  return (
    <div className="tap">
      <div className={`tap-headline ${d.level || d.status}`}>
        <span aria-hidden="true">{icon}</span>
        <div>
          {d.level && <b>{TAP_LEVEL_WORD[d.level]}</b>}
          <p>{d.headline}</p>
        </div>
      </div>

      {d.status === 'ok' && (
        <>
          <div className="tap-system">
            <span>Water system</span>
            <b>{d.systemName}</b>
            <small>{d.pwsid}{d.populationServed ? ` · serves ${d.populationServed.toLocaleString()} people` : ''}{d.insideBoundary ? '' : ' · nearest system'}</small>
          </div>

          {(d.lead || d.copper) && (
            <div className="tap-metals">
              {d.lead && <Metal name="Lead" m={d.lead} />}
              {d.copper && <Metal name="Copper" m={d.copper} />}
            </div>
          )}

          <div className="tap-section">
            <strong>Health-based violations · last {d.healthWindowYears} years</strong>
            {d.healthViolations.length === 0 ? (
              <p className="tap-none">None on record.</p>
            ) : (
              <ul className="tap-violations">
                {d.healthViolations.map((v, i) => (
                  <li key={i}>
                    <b>{v.contaminant}</b>{v.count > 1 ? ` (×${v.count})` : ''} — {v.what}, {v.period}.{' '}
                    <span className={v.resolved ? 'ok' : 'bad'}>{v.resolved ? `Resolved${v.resolvedOn ? ` ${v.resolvedOn}` : ''}` : 'Not resolved'}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}

      {d.notes?.length > 0 && (
        <ul className="tap-notes">{d.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>
      )}

      {d.reportUrl && (
        <a className="tap-link" href={d.reportUrl} target="_blank" rel="noreferrer">Full EPA report for this water system ↗</a>
      )}
      <div className="caveat">Source: EPA Safe Drinking Water Information System{d.dataAsOf ? ` (updated ${d.dataAsOf})` : ''} and EPA service-area boundaries.</div>
    </div>
  )
}

// 90th-percentile tap samples vs EPA's action level, as a word, a number and a bar (never color alone).
function Metal({ name, m }) {
  const pct = Math.min(100, (m.ppb / m.actionLevelPpb) * 100)
  const value = m.ppb === 0 ? 'Below detection' : `${m.ppb} ppb`
  return (
    <div className="metal">
      <div className="metal-row">
        <b>{name}</b>
        <span className={m.overActionLevel ? 'bad' : 'ok'}>{m.overActionLevel ? 'Above action level' : 'Below action level'}</span>
      </div>
      <div className="metal-bar" role="img" aria-label={`${name}: ${value}; EPA action level ${m.actionLevelPpb} ppb`}>
        <span style={{ width: `${Math.max(pct, 1.5)}%` }} className={m.overActionLevel ? 'bad' : 'ok'} />
      </div>
      <small>{value} · action level {m.actionLevelPpb} ppb · 90th percentile of home tap samples, {m.period}</small>
    </div>
  )
}
