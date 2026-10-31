# Stormwater Flood-Risk Map

A **"What If" flood / runoff-risk map** for suburban homeowners and town planners.

Click a spot → see a **0–100 flood-risk score**, trace **where the runoff flows downhill**, and get an
**AI risk assessment** (Groq) from terrain features. Every assessment is **saved to MongoDB**, so the map
builds up a persistent "flood history" layer. Switch to **3D terrain** to see the real topography.

**Stack:** .NET 10 Web API · React + MapLibre GL (Vite) · Groq (OpenAI-compatible) · MongoDB.

---

## How it works

```
Startup            →  load real DEM (USGS 3DEP, Fuquay-Varina) or synthetic fallback + hydrology in C#
                       · D8 flow direction
                       · flow accumulation (upstream area)
                       · slope + local depression
                       · 0–100 risk score (weighted blend)
GET  /health            →  liveness probe (Render + Docker healthcheck)
GET  /api/map           →  risk points for the frontend
GET  /api/flowpath      →  downhill drainage path from any coordinate
POST /api/assess        →  features → Groq AI → { riskScore, category, summary }  → saved to Mongo
GET  /api/assessments(+ /near) → recent / geospatial (2dsphere) queries
React                    →  color-coded map, click-to-assess, storm toggle, history markers
```

---

## Run it

### Option A — Docker (recommended)

```bash
docker compose --profile core --profile api up -d
```

Brings up MongoDB (`mongo:8`) and the API (mapped to `http://localhost:5000`). The API runs
non-root on `:8080` inside the container; Mongo is published on `27018`.

```bash
# then start the frontend
cd frontend && npm install && npm run dev   # http://localhost:5173
```

Stop it with `docker compose --profile core --profile api down`.

### Option B — without Docker

```bash
cd backend && dotnet run          # http://localhost:5000  (Mongo optional; fails soft)
cd frontend && npm install && npm run dev
```

---

## Project conventions (aligned with the Commons repo)

- **.NET 10** via `global.json` (SDK `10.0.100`, `rollForward: latestMajor`).
- **Central Package Management** — `Directory.Packages.props` (`MongoDB.Driver` 3.11.1).
- **`Directory.Build.props`** — `TreatWarningsAsErrors`, `bin\$(Platform)\$(Configuration)\`.
- **Docker** — multi-stage build, non-root `$APP_UID`, `ASPNETCORE_URLS=http://+:8080`, curl healthcheck.
- **Config keys** — `MongoDb__ConnectionString`, `MongoDb__DatabaseName`, `AI__*` (env vars override `appsettings.json`).

---

## Configuration

`backend/appsettings.json` (all overridable by environment variables):

| Section | Key | Env var | Meaning |
|---|---|---|---|
| `Terrain` | `Rows`/`Cols` | — | Grid size (used by the synthetic fallback) |
| `Terrain` | `OriginLat`/`OriginLng` | — | Grid corner (fallback only) |
| `Terrain` | `PixelDegrees` | — | Cell size in degrees (fallback only) |
| `Terrain` | `DemFile` | — | Preprocessed DEM JSON (default `data/terrain.json`) |
| `MongoDb` | `ConnectionString` | `MongoDb__ConnectionString` | `mongodb://…` or Atlas `mongodb+srv://…` |
| `MongoDb` | `DatabaseName` | `MongoDb__DatabaseName` | DB name (default `floodrisk`) |
| `AI` | `BaseUrl` | `AI__BaseUrl` | OpenAI-compatible endpoint |
| `AI` | `Model` | `AI__Model` | Model id |
| `AI` | `ApiKey` | `AI__ApiKey` | API key |

> **Security:** the Groq key is in `appsettings.json` for demo convenience (and gets baked into the
> Docker image). Before sharing/committing the repo, remove it and set `AI__ApiKey` as an env var
> instead (Render dashboard, or a gitignored `.env` for docker-compose).

---

## Deploy

- **Backend → Render**: use the included `render.yaml` (Docker blueprint). Set `AI__ApiKey` and
  `MongoDb__ConnectionString` in the dashboard. Render sets `ASPNETCORE_URLS=http://+:10000`.
- **Database → MongoDB Atlas**: free M0 cluster; allowlist Render's IP; paste the `mongodb+srv://` URI
  as `MongoDb__ConnectionString`. The app auto-creates the collection + `2dsphere` index.
- **Frontend → Vercel**: root dir `frontend`, Framework Vite, env var
  `VITE_API_URL = https://<render-app>.onrender.com/api`.

---

## API reference

| Method | Path | Body / params | Returns |
|---|---|---|---|
| GET | `/health` | — | `{ status }` |
| GET | `/api/geocode` | `?address=…` | `{ lat, lng, matchedAddress }` (Census + Photon fallback) |
| GET | `/api/map` | `?stride=3` | grid + risk points |
| GET | `/api/flowpath` | `?lat=&lng=` | `[{ lat, lng }, …]` downhill path |
| POST | `/api/assess` | `{ lat, lng }` | `{ id, features, ai }` (persisted) |
| GET | `/api/assessments` | `?limit=200` | latest assessments |
| GET | `/api/assessments/near` | `?lat=&lng=&radius=800` | nearby (meters, 2dsphere) |

---

## Study area & real terrain

The app ships with a **real USGS 3DEP DEM (10 m)** for **Fuquay-Varina, NC** in
`backend/data/terrain.json`, produced by `tools/prepare_dem.py` (downloads from the USGS 3DEP
ImageServer — no key required). `TerrainService` loads it at startup; if the file is absent it falls
back to synthetic terrain.

To point at a **different town**, edit the `SOUTH/NORTH/WEST/EAST` bounds in `tools/prepare_dem.py`
and re-run `python tools/prepare_dem.py`.

Imperviousness (NLCD) is not yet included — the risk score is currently terrain-only
(elevation, slope, flow accumulation, depression).

---

## Honest limitations (say these in the pitch)

- It's a **relative ranking** (a screening tool), not a calibrated flood probability.
- A ~10 m grid can't see a single clogged gutter or backyard swale — it flags *areas* to inspect first.
- Storm-drain data is spotty in open sources; the demo uses terrain + imperviousness only.
