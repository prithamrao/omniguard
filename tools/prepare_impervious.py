"""
Derive an imperviousness grid for the study area from OpenFreeMap vector tiles
(building points, built-up land-use polygons, and roads), rasterized onto the same
grid as terrain.json, then merged in as an "imperviousness" array.

Imperviousness (0-100):
  built-up land use (residential/commercial/industrial/retail/school) -> 55
  buildings (housenumber points buffered ~12m)                       -> 95
  roads (transportation lines buffered ~10m)                         -> 95
  natural land (wood, grass, wetland, farmland, ...)                 -> 0

Run after tools/prepare_dem.py. No API key required.
"""
import json
import math
import urllib.request

import mapbox_vector_tile
import numpy as np
from shapely.affinity import affine_transform
from shapely.geometry import Point, shape
from shapely.vectorized import contains as vec_contains

SOUTH, NORTH = 35.542, 35.616
WEST, EAST = -78.813, -78.723
Z = 14
TILE_URL = "https://tiles.openfreemap.org/planet/20261025_080012_pt/{z}/{x}/{y}.pbf"
TERRAIN = "backend/data/terrain.json"

BUILTUP_CLASSES = {"residential", "commercial", "industrial", "retail", "school"}


def lon_to_x(lng):
    return int((lng + 180) / 360 * 2 ** Z)


def lat_to_y(lat):
    return int((1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * 2 ** Z)


def tile_lat(y):
    n = math.pi * (1 - 2 * y / 2 ** Z)
    return math.degrees(math.atan(math.sinh(n)))


def fetch_geometries():
    x0, x1 = lon_to_x(WEST), lon_to_x(EAST)
    y0, y1 = lat_to_y(NORTH), lat_to_y(SOUTH)
    builtup, roads, buildings = [], [], []
    for tx in range(x0, x1 + 1):
        lon0 = tx / 2 ** Z * 360 - 180
        lon1 = (tx + 1) / 2 ** Z * 360 - 180
        for ty in range(y0, y1 + 1):
            lat0 = tile_lat(ty)   # north edge
            lat1 = tile_lat(ty + 1)  # south edge
            xfact = (lon1 - lon0) / 4096.0
            yfact = (lat1 - lat0) / 4096.0

            def xform(g):
                # tile-local [0,4096] -> lon/lat
                return affine_transform(g, [xfact, 0, 0, yfact, lon0, lat0])

            url = TILE_URL.format(z=Z, x=tx, y=ty)
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 flood-risk-map/1.0"})
            data = mapbox_vector_tile.decode(urllib.request.urlopen(req, timeout=60).read())
            for f in data.get("landuse", {}).get("features", []):
                if f["properties"].get("class") in BUILTUP_CLASSES:
                    builtup.append(xform(shape(f["geometry"])))
            for f in data.get("transportation", {}).get("features", []):
                g = shape(f["geometry"])
                if g.geom_type in ("LineString", "MultiLineString"):
                    roads.append(xform(g).buffer(0.0001))  # ~10 m
            for f in data.get("housenumber", {}).get("features", []):
                g = shape(f["geometry"])
                if g.geom_type == "Point":
                    buildings.append(xform(g).buffer(0.00012))  # ~12 m
    return builtup, roads, buildings


def rasterize(geoms, rows, cols, origin_lat, origin_lng, pixel_lat, pixel_lng):
    grid = np.zeros((rows, cols), dtype=np.uint8)
    for k, g in enumerate(geoms):
        if not g.is_valid:
            g = g.buffer(0)
        minx, miny, maxx, maxy = g.bounds
        i0 = max(0, int((maxy - origin_lat) / pixel_lat))  # north (pixel_lat negative)
        i1 = min(rows - 1, int((miny - origin_lat) / pixel_lat))  # south
        j0 = max(0, int((minx - origin_lng) / pixel_lng))
        j1 = min(cols - 1, int((maxx - origin_lng) / pixel_lng))
        if i1 < i0 or j1 < j0:
            continue
        js, is_ = np.meshgrid(np.arange(j0, j1 + 1), np.arange(i0, i1 + 1))
        lngs = origin_lng + (js + 0.5) * pixel_lng
        lats = origin_lat + (is_ + 0.5) * pixel_lat
        mask = vec_contains(g, lngs.ravel(), lats.ravel()).reshape(lngs.shape)
        grid[i0:i1 + 1, j0:j1 + 1] |= mask.astype(np.uint8)
    return grid


def main():
    terrain = json.load(open(TERRAIN))
    rows, cols = terrain["rows"], terrain["cols"]
    olat, olng = terrain["originLat"], terrain["originLng"]
    plat, plng = terrain["pixelLat"], terrain["pixelLng"]

    print("fetching tiles…")
    builtup, roads, buildings = fetch_geometries()
    print(f"  builtup={len(builtup)} roads={len(roads)} buildings={len(buildings)}")

    b = rasterize(builtup, rows, cols, olat, olng, plat, plng)
    r = rasterize(roads, rows, cols, olat, olng, plat, plng)
    h = rasterize(buildings, rows, cols, olat, olng, plat, plng)

    impervious = np.zeros((rows, cols))
    impervious = np.maximum(impervious, b * 55)
    impervious = np.maximum(impervious, r * 95)
    impervious = np.maximum(impervious, h * 95)

    terrain["imperviousness"] = [round(float(v), 1) for v in impervious.ravel()]
    json.dump(terrain, open(TERRAIN, "w"))

    pct = float((impervious > 0).mean() * 100)
    print(f"impervious coverage: {pct:.1f}% of cells; mean={impervious.mean():.1f}")
    print(f"wrote imperviousness into {TERRAIN}")


if __name__ == "__main__":
    main()
