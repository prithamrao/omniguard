# Stormwater Flood-Risk Map — set up on a new computer

Two ways to get this app running somewhere else:

- **Part A — the exact app (recommended).** Clone this repo, add your keys, run it. You get *exactly* this code. ~15 minutes.
- **Part B — rebuild it from scratch with prompts.** A sequence of prompts for Claude Code that recreates the same features and avoids every bug we hit. AI-written code is never byte-identical between runs, so expect the same *behavior*, not the same files.

---

## Part A — the exact app

### 1. Install once

| Tool | Version | Get it |
|---|---|---|
| Git | any | https://git-scm.com |
| Docker Desktop | any recent | https://www.docker.com/products/docker-desktop |
| Node.js | 20 or newer | https://nodejs.org |
| .NET SDK | 10.x (only if running the API without Docker) | https://dotnet.microsoft.com/download |

### 2. Get your keys (keep them out of chat and out of git)

| `.env` name | What it's for | Where |
|---|---|---|
| `GROQ_API_KEY` | AI second opinion | https://console.groq.com/keys |
| `GEOAPIFY_KEY` | Address search + autocomplete | https://myprojects.geoapify.com |
| `REPORTALL_CLIENT_KEY` | Exact property lot, year built, building size | https://reportallusa.com/account/api |
| `GOOGLE_SOLAR_KEY` | Current aerial surface for lot drainage | https://console.cloud.google.com/apis/library/solar.googleapis.com → enable → Credentials → API key |

Also set `HUMAN_CHECK_MODE` (default `code`: a typed-code captcha before the app) and `SESSION_SIGNING_KEY` (any random string of 32+ characters).

Only Groq is strictly required. Without the others the app still runs:

- **No Geoapify:** search falls back to the US Census geocoder.
- **No ReportAll:** the location is marked "approximate".
- **No Google:** routing falls back to lot-grading rules.

### 3. Clone, add keys, run

```bash
git clone https://github.com/prithamrao/omniguard.git
cd omniguard
cp .env.example .env        # then open .env and paste your keys
docker compose --profile core --profile api up -d --build
cd frontend
npm install
npm run dev
```

Open http://localhost:5173, type the code on the security screen, and search a US home address you know. You should see:

- the lot outlined in yellow
- a cyan path to the street and a blue path to the pond behind
- a front/back drainage split
- Terrain and AI scores

Stop everything with `docker compose --profile core --profile api down`.

### 4. One prompt that does Part A for you (paste into Claude Code on the new computer)

> Clone https://github.com/prithamrao/omniguard into my Documents folder and get it running locally. Check that Git, Docker Desktop and Node 20+ are installed and tell me what's missing before going further. Copy `.env.example` to `.env` and STOP so I can paste my own keys into it myself — never ask me to paste keys into chat. When I say "keys added", run `docker compose --profile core --profile api up -d --build`, wait for http://localhost:5000/health to return ok, then `npm install` and `npm run dev` in `frontend`. Open http://localhost:5173 in the browser, search [a US home address], and confirm the yellow lot outline, the runoff paths, the front/back drainage split and both scores appear. Report anything that fails with the exact error.

### 5. Deploy (optional)

- **API on Render:** New → Blueprint → this repo (uses `render.yaml`).
  - Set `AI__ApiKey`, `MongoDb__ConnectionString` (e.g. a free MongoDB Atlas cluster), `Geoapify__ApiKey`, `ReportAll__ClientKey` and `GoogleSolar__ApiKey` in the service's Environment tab.
  - Use double underscores exactly as written.
- **Frontend on Vercel:** import the `frontend` folder and set `VITE_API_URL=https://<your-render-service>.onrender.com/api`.

---

## Part B — rebuild from scratch with prompts

Run these **in order** in Claude Code, one per message, in an empty folder. Wait for each to finish and test before the next. Put keys in `.env` yourself; never paste them into chat. Each prompt includes lessons we learned the hard way.

### Prompt 1 — Skeleton

> Create a stormwater flood-risk web app. Backend: .NET 10 minimal API (`backend/`, project `FloodRisk.Api`) with central package management (`Directory.Packages.props`, MongoDB.Driver), `Directory.Build.props` with TreatWarningsAsErrors, a multi-stage non-root Dockerfile listening on 8080 with a curl healthcheck on `/health`. Frontend: React 18 + Vite + MapLibre GL (`frontend/`) with a left panel (search box with Go and Clear, basemap buttons Streets/Light/Dark/Satellite using OpenFreeMap styles and Esri World Imagery, a 3D terrain toggle using AWS terrarium tiles, Normal rain / Heavy storm (2.5 in) buttons, and a Low/Moderate/High/Severe legend). Vite proxies `/api` to `process.env.API_PROXY || 'http://localhost:5000'`. Add `docker-compose.yml` with profiles `core` (mongo:8 on host port 27018) and `api` (API on host port 5000) that passes these keys from a gitignored root `.env`: `AI__ApiKey=${GROQ_API_KEY}`, `Geoapify__ApiKey=${GEOAPIFY_KEY}`, `ReportAll__ClientKey=${REPORTALL_CLIENT_KEY}`, `GoogleSolar__ApiKey=${GOOGLE_SOLAR_KEY}`. Add `.env.example`, a Render `render.yaml` (secrets as `sync: false`), and a README. MongoDB must fail soft: the app runs if Mongo is down.

### Prompt 2 — Terrain and hydrology

> Add terrain and hydrology, all in our own C# (no GIS library).
>
> **Terrain:** `DemService` fetches an 8 km square of elevation around a point. US points use USGS 3DEP exportImage (bare-earth, float32 GeoTIFF, 600×600 grid ~13 m cells, parsed with a minimal TIFF reader); elsewhere use AWS terrarium tiles. IMPORTANT: center each area on a fixed tile (round lat/lng to 0.02°), never on the clicked point, and cache it with `Lazy<Task>` so concurrent requests share one fetch — otherwise the same house gets different scores depending on click order.
>
> **Hydrology** (`HydrologyService`):
> - priority-flood depression filling (Barnes 2014) with a 1e-4 epsilon
> - D8 flow direction on the filled surface
> - slope in percent on the real surface, using the true cell size in meters
> - flow accumulation by sorting cells high to low
> - ponding depth = filled − original (0 below 1 cm)
> - "relative low" = 15×15 box-mean minus elevation, via a summed-area table
>
> **Risk score:** 0–100 on FIXED physical scales, never per-area min/max: log(accumulation)/log(2000), flatness (5% slope = 0), ponding saturating at 0.5 m, relative lowness at 1.5 m, paved %. Weights 0.30 accumulation, 0.20 flatness, 0.25 max(pond, low) + 0.10 min(pond, low), 0.15 paving.
>
> **Flow-path bug to avoid:** when stepping downhill, read both row and column direction from the CURRENT cell before moving.

### Prompt 3 — Map layers from vector tiles

> Add `ImperviousService` that reads OpenFreeMap vector tiles (z14) with a minimal MVT protobuf decoder: landuse (residential/commercial/industrial/retail/school → 55%), housenumber points with their `housenumber` tag, transportation lines, and water polygons. Rasterize them onto the same grid as the elevation: paved %, a water mask, and a street mask (classes motorway/trunk/primary/secondary/tertiary/minor — NOT service, which is driveways and parking aisles). Also keep street centerlines as vectors. CRITICAL BUG TO AVOID: decode multi-part geometries part by part (a new part starts at every MoveTo). Joining parts end to end creates fake road segments cutting across back yards.

### Prompt 4 — Address search that lands on the right house

> Add geocoding.
>
> **Search:** `/api/geocode` tries Geoapify search first and accepts only `result_type` building or amenity, then the US Census geocoder, then Photon. It always returns the house number. `/api/suggest` uses Geoapify autocomplete filtered to the US, with keyboard navigation in the dropdown (arrows, Enter, Escape). Picking a suggestion must call the same `/api/geocode` with the full label, so typing and picking always land on identical coordinates.
>
> **Parcel lookup:** add `ParcelService` using ReportAll's `/api/parcels` (`client` key, `v=9`, `spatial_intersect` WKT, `si_srid=4326`, geometry in `geom_as_wkt`). For a search, find the lot whose address has the SAME house number and street name, widening the search in steps (the point itself, 80 m, 250 m) so ReportAll's per-page cap never cuts the right lot off. For a map click, use the lot under the cursor. Return the address, the lot outline, the centroid, `year_built`, `bldg_sqft` and `land_cover`.
>
> **Caching:** cache every lookup in memory then Mongo, including "no lot here", with a versioned cache key.
>
> **Why the parcel step matters:** free geocoders spread house numbers evenly along the street, so they often land on the neighbor's house.

### Prompt 5 — The assessment

> Add `POST /api/assess` that does the following:
>
> - **Find the lot.** Assess at the parcel centroid when a lot is found; otherwise at the geocoded point.
> - **Estimate paved %** per lot as the larger of:
>   - the building footprint (bldg_sqft / 1.6) plus 500 sq ft of driveway, over the lot area
>   - the NLCD land-cover mix weighted by class (High 90, Medium 65, Low 35, Open 10)
>
>   Show which source was used.
> - **Compute both scenarios.** Normal rain is 1 inch; heavy storm is 2.5 inches. The storm score is 1.6× normal plus an overflow bump.
> - **Overflow check.** For a low spot, compare runoff (rain × 0.5 × its catchment area) with the volume it holds (sum of ponding depths × cell area), and mark it as overflowing when runoff is larger.
> - **Write plain-language observations,** each tagged "Concern:", "In its favor:" or "Info:". Only Concern facts may become issues.
> - **Get the AI second opinion.** Call Groq (`openai/gpt-oss-120b`, OpenAI-compatible, temperature 0, seed 42, `json_object` response format) ONCE for both scenarios. It returns:
>   - riskScore and stormRiskScore — the code enforces storm ≥ normal
>   - stormNote, summary, issues (semicolon-separated) and recommendation
>
>   The AI must NEVER see our formula's score; otherwise it just copies it. Ban the words impervious, depression and flow accumulation.
> - **Cache AI answers.** Key by grid cell + a stable FNV hash of the facts (not `string.GetHashCode`, which changes every run), in memory and in Mongo. A revisit then returns the identical record, and new facts trigger a fresh evaluation.

### Prompt 6 — Where the water goes

> Add runoff routing:
>
> 1. **Follow the water downhill.** Before reaching a street, it can stop at:
>    - a resident-reported pond or drain
>    - a mapped water body
>    - a dip at least 0.15 m deep
>
>    Once it reaches a street, curbs and gutters carry it to the storm drains. Ignore dips beyond the street, but report a pond within 400 m past it as the likely outfall, drawn dotted and labeled approximate.
> 2. **Detect stale terrain.** Get the survey year from the USGS product catalog (TNM Access `products` API: 1 m DEMs and lidar point clouds, year parsed from the project name). Also check the 3DEP lidar index, which misses newer projects, and take the newest year. If the home's year_built is newer than the survey, the terrain shows the land before grading. In that case, route by lot grading instead: roof and front yard go to the nearest point on a street centerline; the back yard goes to the nearest pond within 200 m.
> 3. **Use the current aerial surface when available.** With a Google Solar API key, fetch `dataLayers:get` (`view=DSM_LAYER`, radius 100 m, 0.5 m pixels, `requiredQuality=LOW`) and download the DSM GeoTIFF: Deflate-compressed, tiled, float32, big-endian, UTM via ModelTransformation, EPSG from GeoKey 3072. Then:
>    - smooth it with a 3-pixel box and fill pits
>    - sample the lot every 2 m and trace each sample until it leaves the lot
>    - classify each exit as front or back by its direction relative to the street
>    - report the front/back percentages plus both paths, and follow the back path to a pond on or within 40 m of its end
>    - trim the front path where it comes closest to the street
>    - write your own UTM forward and inverse conversion

### Prompt 7 — Map pins, hover cards, and the panel

> On the map:
>
> - **Pins.** One DOM-marker pin per issue at its real location: home (red when it has problems), street, low spot or pond, back-yard end and uphill inflow. Each pin's hover card lists that spot's issues in plain language. Put the teardrop rotation on an INNER element, because MapLibre owns the marker's transform.
> - **Lines and fills.**
>   - lot outline: yellow over a dark casing
>   - front path: cyan dashes; back path: blue dashes; storm-drain continuation: light dotted line
>   - pond area: a fill with no per-cell outline, red and pulsing when it overflows
> - **Framing.** After a search, fit the map to the house plus its pins.
>
> In the panel:
>
> - the official lot address ("Common area" for house number 0)
> - badges: storm scenario; "Aerial surface YYYY-MM (lot graded, before the home was built) · public survey YYYY"; and "approximate location" when no lot was found
> - "Terrain model" and "AI engineer" score cards, each showing a category word and never color alone
> - the AI's storm note, issues as a bulleted list, and "What to do"
> - "Where your water goes" with a front/back share bar and a "Show pond" button
> - feature chips: elevation, slope %, uphill area in sq ft or acres, paved % and its source, height vs. neighbors
>
> Switching scenario re-assesses the current property.

### Prompt 8 — Resident reports

> Add "Missing on the map? + Pond / + Storm drain". After clicking a button, the next map click places a report:
>
> - `POST /api/reports` saves it in Mongo, with an in-memory fallback.
> - `GET /api/reports/near` loads reports around a point, and `DELETE /api/reports/{id}` removes one.
> - Reports render as square pins with a popup and a Remove button. The click handler must stop propagation AND toggle the popup itself.
> - Reports feed the runoff routing: a pond covers 20 m around its pin; a drain covers its own cell and its neighbors.
> - Adding or removing a report re-assesses the property.

### Prompt 9 — End-to-end check

> Run the full app locally and test it in the browser. For two real US addresses (ideally one home built in the last few years):
>
> 1. Search via autocomplete.
> 2. Click three other houses.
> 3. Search the original address by typing it.
>
> The panel must match field for field. Then:
>
> - Toggle Heavy storm: both scores must go up and a storm note must appear.
> - Add and remove a storm drain on the street: the route must change, then revert exactly.
> - Confirm the street pin lies outside the lot, on the road.
> - Confirm no runoff path crosses a neighbor's lot.
>
> Fix anything that fails and show me screenshots.

---

## Why Part A is the real answer

The prompts above capture every decision and fix behind this app. But the only way to get the *same exact* result is the code itself, which is public at https://github.com/prithamrao/omniguard.
