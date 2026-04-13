"""
test_analyzer.py

End-to-end test for image_analyzer.py — no telescope or NINA required.

What it simulates
-----------------
A real GRB observation session:
  1. Trigger fires at t=0
  2. NINA captures 4 frames at t+6min, t+9min, t+12min, t+18min after trigger
  3. Each frame is a real DSS FITS image of the Crab Nebula (bright, lots of sources)
  4. Frames are saved to a temp folder with realistic file timestamps
  5. A grb_captures Firestore document is pushed (same format as GRBCaptureInstruction)
  6. image_analyzer.py picks it up within 30s and runs the full pipeline:
        - Forced aperture photometry at the GRB position
        - SNR measurement per frame
        - Magnitude or 3sigma upper limit per frame
        - Gaia calibration + photometric uncertainty
        - Power-law decay fit across the 4 frames

Usage
-----
    cd GCN_parser
    python test_analyzer.py

Then in a separate terminal:
    python image_analyzer.py

Expected output in image_analyzer.py
--------------------------------------
  [Analyzer] Found 4 FITS file(s) for GRB_TEST_260413
  [Analyzer]   frame_t06min.fits -> DETECTION  mag=~16.xx +/- 0.xxx  SNR=~xx
  [Analyzer]   frame_t09min.fits -> ...
  [Analyzer]   frame_t12min.fits -> ...
  [Analyzer]   frame_t18min.fits -> ...
  [Analyzer] Power-law decay: alpha = x.xxx +/- x.xxx
  [Analyzer] Done: GRB_TEST_260413 | detections=4 | upper_limits=0 | alpha=x.xxx
"""

import os
import time
import tempfile
import shutil
from datetime import datetime, timezone, timedelta

import firebase_admin
from firebase_admin import credentials, firestore
from astropy.coordinates import SkyCoord
from astroquery.skyview import SkyView
from astropy.io import fits
import astropy.units as u

# ── Firebase init ─────────────────────────────────────────────────────────────
print("[Test] Initialising Firebase...")
cred = credentials.Certificate("firebase_service_account.json")
firebase_admin.initialize_app(cred)
db = firestore.client()

# ── GRB parameters ────────────────────────────────────────────────────────────
# We use the Crab Nebula as a stand-in for a GRB field:
#   - bright, well-calibrated, lots of Gaia reference stars in the field
#   - DSS image downloads reliably
GRB_NAME  = "GRB_TEST_260413"
GRB_RA    = 83.8221    # Crab Nebula RA  (degrees)
GRB_DEC   = 22.0214    # Crab Nebula Dec (degrees)
GRB_ERROR = 3.0        # position uncertainty (arcmin) — tight enough to pass filter

# Simulated trigger time — 30 minutes ago so delta_t values are positive and sensible
TRIGGER_TIME = datetime.now(timezone.utc) - timedelta(minutes=30)

# Simulated frame offsets after trigger (minutes)
# Spread enough to attempt a power-law fit (needs >3 min baseline, >=3 detections)
FRAME_OFFSETS_MIN = [6, 9, 12, 18]

# ── Download DSS images ───────────────────────────────────────────────────────
coord = SkyCoord(ra=GRB_RA * u.deg, dec=GRB_DEC * u.deg)

print(f"[Test] Downloading {len(FRAME_OFFSETS_MIN)} DSS FITS frames of Crab Nebula field...")
print("       (this takes ~30 seconds — each frame is a separate SkyView query)")

fits_dir = tempfile.mkdtemp(prefix="grb_test_")
print(f"[Test] Temp directory: {fits_dir}")

frame_paths = []
for i, offset_min in enumerate(FRAME_OFFSETS_MIN):
    frame_time = TRIGGER_TIME + timedelta(minutes=offset_min)
    fname      = f"frame_t{offset_min:02d}min.fits"
    fpath      = os.path.join(fits_dir, fname)

    print(f"[Test]   Downloading frame {i+1}/{len(FRAME_OFFSETS_MIN)}: {fname} "
          f"(dt = +{offset_min} min after trigger)...")

    images = SkyView.get_images(
        position=coord,
        survey=["DSS"],
        radius=15 * u.arcmin,
        pixels=512
    )

    if not images:
        print(f"[Test] ERROR: Failed to download DSS frame {i+1} — check internet connection.")
        shutil.rmtree(fits_dir, ignore_errors=True)
        exit(1)

    # Inject DATE-OBS into the FITS header so image_analyzer.py picks up the
    # correct observation timestamp for computing delta_t and the power-law fit
    hdul = images[0]
    hdul[0].header["DATE-OBS"] = frame_time.strftime("%Y-%m-%dT%H:%M:%S")
    hdul[0].header["OBJECT"]   = GRB_NAME
    hdul.writeto(fpath, overwrite=True)

    # Also set the file mtime to match (fallback if header read fails)
    os.utime(fpath, (frame_time.timestamp(), frame_time.timestamp()))

    frame_paths.append(fpath)
    print(f"[Test]   Saved: {fpath}")

print(f"[Test] All {len(frame_paths)} frames ready in {fits_dir}")

# ── Push grb_captures document to Firestore ───────────────────────────────────
# Matches the exact format that FirestoreCapturePoster.cs produces,
# including trigger_time which was added to enable delta_t and power-law fitting
print(f"\n[Test] Pushing grb_captures document for {GRB_NAME}...")

test_record = {
    "grb_name":         GRB_NAME,
    "ra_deg":           GRB_RA,
    "dec_deg":          GRB_DEC,
    "error_arcmin":     GRB_ERROR,
    "trigger_time":     TRIGGER_TIME.isoformat(),
    "capture_time":     datetime.now(timezone.utc).isoformat(),
    "exposure_count":   len(FRAME_OFFSETS_MIN),
    "exposure_seconds": 30,
    "fits_directory":   fits_dir,
    "status":           "pending_analysis",
}

_, doc_ref = db.collection("grb_captures").add(test_record)

print(f"[Test] Document ID : grb_captures/{doc_ref.id}")
print(f"\n[Test] ── Summary ──────────────────────────────────────────────────")
print(f"[Test]   GRB name    : {GRB_NAME}")
print(f"[Test]   Coordinates : RA={GRB_RA}  Dec={GRB_DEC}  Error={GRB_ERROR} arcmin")
print(f"[Test]   Trigger time: {TRIGGER_TIME.strftime('%Y-%m-%dT%H:%M:%S UTC')}")
print(f"[Test]   Frames      : {len(FRAME_OFFSETS_MIN)} x 30s at "
      f"dt = {FRAME_OFFSETS_MIN} min after trigger")
print(f"[Test]   FITS dir    : {fits_dir}")
print(f"\n[Test] Now run in a separate terminal:")
print(f"[Test]     python image_analyzer.py")
print(f"[Test] It will pick up this document within 30 seconds.")
print(f"\n[Test] Expected results in Firestore (grb_captures/{doc_ref.id}):")
print(f"[Test]   light_curve[]        -> 4 entries (magnitude + SNR + delta_t per frame)")
print(f"[Test]   decay_alpha          -> temporal decay index alpha")
print(f"[Test]   decay_alpha_error    -> 1-sigma uncertainty on alpha")
print(f"[Test]   deepest_upper_limit  -> faintest non-detection (if any)")
print(f"\n[Test] Temp files stay in {fits_dir} — delete manually when done.")
