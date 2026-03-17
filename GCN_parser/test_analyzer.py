"""
test_analyzer.py

Downloads a real DSS FITS image of the Crab Nebula, saves it to a temp folder,
then pushes a fake grb_captures document pointing at that folder so
image_analyzer.py can process it — no NINA, no telescope needed.

Usage:
    python test_analyzer.py
"""

import os
import tempfile
import firebase_admin
from firebase_admin import credentials, firestore
from datetime import datetime, timezone
from astropy.coordinates import SkyCoord
from astroquery.skyview import SkyView
import astropy.units as u

# ── Firebase init ─────────────────────────────────────────────────────────────
cred = credentials.Certificate("firebase_service_account.json")
firebase_admin.initialize_app(cred)
db = firestore.client()

# ── Download a real DSS FITS file to use as test image ───────────────────────
# Crab Nebula — bright, lots of sources, well-known
RA  = 83.8221
DEC = 22.0214
coord = SkyCoord(ra=RA * u.deg, dec=DEC * u.deg)

print("[Test] Downloading DSS FITS image of Crab Nebula field...")
images = SkyView.get_images(position=coord, survey=["DSS"], radius=15 * u.arcmin, pixels=512)
if not images:
    print("[Test] Failed to download DSS image — check internet connection.")
    exit(1)

fits_dir = tempfile.mkdtemp(prefix="grb_test_")
fits_path = os.path.join(fits_dir, "test_GRB_001.fits")
images[0].writeto(fits_path, overwrite=True)
print(f"[Test] Saved FITS to: {fits_path}")

# ── Push test record to Firestore ─────────────────────────────────────────────
test_record = {
    "grb_name":         "GRB_TEST_PHOTOMETRY",
    "ra_deg":           RA,
    "dec_deg":          DEC,
    "error_arcmin":     5.0,
    "capture_time":     datetime.now(timezone.utc).isoformat(),
    "exposure_count":   3,
    "exposure_seconds": 30,
    "fits_directory":   fits_dir,   # analyzer will find the FITS here
    "status":           "pending_analysis",
}

doc_ref = db.collection("grb_captures").add(test_record)
print(f"[Test] Pushed record: {doc_ref[1].id}")
print("[Test] Run image_analyzer.py — it will pick this up within 30 seconds.")
print(f"[Test] FITS directory: {fits_dir}")
