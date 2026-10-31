"""
Download a real DEM (USGS 3DEP) for a bounding box and emit terrain.json for the app.

Output schema (matches TerrainService's loader):
    { rows, cols, originLat, originLng, pixelLat, pixelLng, elevations: [row-major, row 0 = north] }

The app loads backend/data/terrain.json if present, else falls back to synthetic terrain.
Run once, commit the JSON. No key required (USGS 3DEP ImageServer).
"""
import json
import math
import urllib.request

import numpy as np
import tifffile

# Fuquay-Varina, NC (~8 km square, ~10 m resolution).
SOUTH, NORTH = 35.542, 35.616
WEST, EAST = -78.813, -78.723
SIZE = 800  # pixels per side -> ~10 m cells

OUT = "backend/data/terrain.json"


def download():
    url = (
        "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage"
        f"?bbox={WEST},{SOUTH},{EAST},{NORTH}"
        "&bboxSR=4326"
        f"&size={SIZE},{SIZE}"
        "&imageSR=4326"
        "&format=tiff"
        "&pixelType=F32"   # fractional meters (falls back mentally to U16 if needed)
        "&f=image"
    )
    req = urllib.request.Request(url, headers={"User-Agent": "flood-risk-map/1.0"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def main():
    raw = download()
    with open("dem_raw.tif", "wb") as f:
        f.write(raw)

    a = tifffile.imread("dem_raw.tif").astype(np.float32)

    # Sanity: nodata / invalid -> replace with the median (inland, so rarely hit).
    valid = a[np.isfinite(a)]
    if valid.size == 0:
        raise SystemExit("No valid elevation values returned.")
    median = float(np.median(valid))
    a[~np.isfinite(a)] = median

    rows, cols = a.shape
    pixel_lat = -(NORTH - SOUTH) / rows
    pixel_lng = (EAST - WEST) / cols

    # Round to 0.1 m — plenty for slope/flow, keeps the JSON compact.
    elevations = [round(float(v), 1) for v in a.ravel()]

    out = {
        "rows": rows,
        "cols": cols,
        "originLat": NORTH,
        "originLng": WEST,
        "pixelLat": round(pixel_lat, 9),
        "pixelLng": round(pixel_lng, 9),
        "elevations": elevations,
    }

    with open(OUT, "w") as f:
        json.dump(out, f)

    print(f"shape={a.shape} elev min={a.min():.1f} max={a.max():.1f} mean={a.mean():.1f}")
    print(f"origin=({NORTH},{WEST}) pixelLat={pixel_lat:.7f} pixelLng={pixel_lng:.7f}")
    print(f"wrote {OUT} ({len(raw)} bytes downloaded)")


if __name__ == "__main__":
    main()
