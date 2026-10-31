import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.jsx'
import HumanGate from './HumanGate.jsx'
import './index.css'

// Note: StrictMode intentionally omitted — its double-mount in dev breaks MapLibre's
// initial "load" event (the map never reports loaded, so pitch/terrain animation stalls).
ReactDOM.createRoot(document.getElementById('root')).render(
  <HumanGate>
    <App />
  </HumanGate>,
)
