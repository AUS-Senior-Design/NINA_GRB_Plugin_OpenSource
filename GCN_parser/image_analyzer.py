"""
image_analyzer.py

Watches Firestore grb_captures collection for status == "pending_analysis".
For each capture record:
  1. Finds the FITS files saved by NINA around the capture time
  2. Runs aperture photometry on each FITS file
  3. Calibrates against Gaia reference stars → optical magnitude
  4. Checks for a new source at the GRB coordinates
  5. Writes results back to Firestore

Requirements:
    pip install firebase-admin astroquery astropy numpy photutils rawpy
"""

import glob
import os
import time
import warnings
import numpy as np
from datetime import datetime, timezone, timedelta

import firebase_admin
from firebase_admin import credentials, firestore
from google.cloud.firestore_v1.base_query import FieldFilter

from astropy.coordinates import SkyCoord
from astropy.io import fits
from astropy.stats import sigma_clipped_stats
from astropy.wcs import WCS, FITSFixedWarning
import astropy.units as u

try:
    import rawpy
    HAS_RAWPY = True
except ImportError:
    HAS_RAWPY = False
    print("[Analyzer] Warning: rawpy not installed — Canon RAW files (.cr2/.cr3) won't be processed. Run: pip install rawpy")

from astroquery.skyview import SkyView
from astroquery.simbad import Simbad
from astroquery.gaia import Gaia

from photutils.detection import DAOStarFinder
from photutils.aperture import CircularAperture, aperture_photometry

# ── Firebase init ─────────────────────────────────────────────────────────────
cred = credentials.Certificate("firebase_service_account.json")
firebase_admin.initialize_app(cred)
db = firestore.client()

POLL_INTERVAL_SECONDS = 30
APERTURE_RADIUS_PX    = 8.0   # aperture radius in pixels
SEARCH_WINDOW_MIN     = 15    # look for FITS files within ±15 min of capture time
Gaia.ROW_LIMIT        = 50


# ── FITS file discovery ───────────────────────────────────────────────────────

def find_fits_files(fits_directory, capture_time_str):
    """Return FITS files in fits_directory saved within SEARCH_WINDOW_MIN of capture_time."""
    try:
        capture_time = datetime.fromisoformat(capture_time_str.replace("Z", "+00:00"))
    except Exception:
        capture_time = datetime.now(timezone.utc)

    window = timedelta(minutes=SEARCH_WINDOW_MIN)
    found = []
    for pattern in ["*.fits", "*.fit", "*.FITS", "*.FIT", "*.xisf", "*.cr2", "*.CR2", "*.cr3", "*.CR3"]:
        found.extend(glob.glob(os.path.join(fits_directory, "**", pattern), recursive=True))

    recent = list({
        f for f in found
        if abs((datetime.fromtimestamp(os.path.getmtime(f), tz=timezone.utc) - capture_time).total_seconds())
           < window.total_seconds()
    })
    return sorted(recent, key=os.path.getmtime)


# ── Image loader (FITS + Canon RAW) ──────────────────────────────────────────

def load_image(path):
    """Load image data and header from FITS or Canon RAW file."""
    ext = os.path.splitext(path)[1].lower()
    if ext in ('.cr2', '.cr3'):
        if not HAS_RAWPY:
            raise RuntimeError("rawpy not installed — cannot read RAW files")
        with rawpy.imread(path) as raw:
            # Use the raw Bayer data as grayscale (no debayering needed for photometry)
            data = raw.raw_image_visible.astype(float)
        return data, {}   # no FITS header → no WCS
    else:
        with fits.open(path) as hdul:
            return hdul[0].data.astype(float), hdul[0].header


# ── Photometry ────────────────────────────────────────────────────────────────

def measure_magnitude(fits_path, ra, dec, error_arcmin):
    """
    Perform aperture photometry at the GRB position and calibrate against Gaia stars.
    Returns (magnitude, note_string) or (None, reason_string).
    """
    data, header = load_image(fits_path)

    # ── 1. Background subtraction ─────────────────────────────────────────────
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        _, median, std = sigma_clipped_stats(data, sigma=3.0)
    data_sub = data - median

    # ── 2. Source detection ───────────────────────────────────────────────────
    daofind = DAOStarFinder(fwhm=4.0, threshold=5.0 * std)
    sources = daofind(data_sub)
    if sources is None or len(sources) == 0:
        return None, "No sources detected in image"

    # ── 3. WCS — locate GRB in pixel space ───────────────────────────────────
    has_wcs = False
    grb_x = grb_y = None
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", FITSFixedWarning)
            wcs = WCS(header)
        if wcs.has_celestial:
            grb_coord       = SkyCoord(ra * u.deg, dec * u.deg)
            grb_x, grb_y   = wcs.world_to_pixel(grb_coord)
            has_wcs         = True
    except Exception:
        pass

    if not has_wcs:
        # NINA was pointing at GRB, so it should be near image centre
        grb_x, grb_y = data.shape[1] / 2.0, data.shape[0] / 2.0

    # ── 4. Find closest detected source to GRB position ──────────────────────
    src_x   = np.array(sources["xcentroid"])
    src_y   = np.array(sources["ycentroid"])
    dist_px = np.sqrt((src_x - grb_x) ** 2 + (src_y - grb_y) ** 2)
    nearest = np.argmin(dist_px)
    nearest_dist = dist_px[nearest]

    # Error radius in pixels (rough: 1 arcsec ~ 1 px for typical setups)
    error_px = error_arcmin * 60.0
    if nearest_dist > error_px:
        return None, (f"No source within error radius "
                      f"(nearest={nearest_dist:.1f}px, radius={error_px:.1f}px)")

    # ── 5. Aperture photometry on candidate ───────────────────────────────────
    target_pos    = [(float(src_x[nearest]), float(src_y[nearest]))]
    target_phot   = aperture_photometry(data_sub, CircularAperture(target_pos, r=APERTURE_RADIUS_PX))
    target_counts = float(target_phot["aperture_sum"][0])
    if target_counts <= 0:
        return None, "Negative counts at target position"

    # ── 6. Gaia calibration (requires WCS) ───────────────────────────────────
    if not has_wcs:
        # Return instrumental magnitude (uncalibrated) as a fallback
        inst_mag = -2.5 * np.log10(target_counts)
        return inst_mag, "Instrumental (uncalibrated) — no WCS/plate solution found"

    try:
        gaia_result = Gaia.query_object_async(coordinate=grb_coord, radius=5 * u.arcmin)
        if gaia_result is None or len(gaia_result) == 0:
            return None, "No Gaia stars found for magnitude calibration"

        ref_mags   = []
        ref_counts = []
        for row in gaia_result:
            g_mag = row["phot_g_mean_mag"]
            if g_mag is None or np.isnan(float(g_mag)):
                continue
            ref_sky = SkyCoord(float(row["ra"]) * u.deg, float(row["dec"]) * u.deg)
            rx, ry  = wcs.world_to_pixel(ref_sky)
            if not (10 < rx < data.shape[1] - 10 and 10 < ry < data.shape[0] - 10):
                continue
            ref_phot   = aperture_photometry(data_sub, CircularAperture([(rx, ry)], r=APERTURE_RADIUS_PX))
            ref_counts_val = float(ref_phot["aperture_sum"][0])
            if ref_counts_val > 0:
                ref_mags.append(float(g_mag))
                ref_counts.append(ref_counts_val)

        if len(ref_mags) < 3:
            return None, f"Too few Gaia reference stars in field ({len(ref_mags)} usable)"

        # Zero-point: ZP = ref_mag + 2.5*log10(ref_counts)
        zp_values  = [m + 2.5 * np.log10(c) for m, c in zip(ref_mags, ref_counts)]
        zero_point = float(np.median(zp_values))
        zp_scatter = float(np.std(zp_values))
        target_mag = zero_point - 2.5 * np.log10(target_counts)

        note = (f"Calibrated with {len(ref_mags)} Gaia stars | "
                f"ZP={zero_point:.2f} (scatter={zp_scatter:.2f} mag)")
        return round(target_mag, 2), note

    except Exception as e:
        return None, f"Gaia calibration error: {e}"


# ── Main analysis function ────────────────────────────────────────────────────

def analyze_capture(doc_ref, data):
    grb_name     = data.get("grb_name", "Unknown")
    ra           = data.get("ra_deg")
    dec          = data.get("dec_deg")
    error_arcmin = data.get("error_arcmin", 5.0)
    fits_dir     = data.get("fits_directory", "")
    capture_time = data.get("capture_time", "")

    if ra is None or dec is None:
        doc_ref.update({"status": "error", "error": "Missing ra_deg or dec_deg"})
        return

    print(f"[Analyzer] Processing {grb_name} — RA={ra:.4f}, Dec={dec:.4f}")
    coord = SkyCoord(ra * u.deg, dec * u.deg)

    result = {"status": "analyzed", "analyzed_at": firestore.SERVER_TIMESTAMP}

    # ── DSS reference + Simbad (always runs) ─────────────────────────────────
    try:
        images = SkyView.get_images(position=coord, survey=["DSS"], radius=10 * u.arcmin, pixels=512)
        if images:
            ref_data  = images[0][0].data.astype(float)
            _, ref_median, ref_std = sigma_clipped_stats(ref_data, sigma=3.0)
            centre_y, centre_x = ref_data.shape[0] // 2, ref_data.shape[1] // 2
            box        = ref_data[centre_y - 5:centre_y + 5, centre_x - 5:centre_x + 5]
            peak_sigma = float((np.max(box) - ref_median) / ref_std) if ref_std > 0 else 0.0
            result.update({
                "dss_peak_sigma":    round(peak_sigma, 2),
                "source_candidate":  peak_sigma > 3.0,
            })
    except Exception as e:
        result["dss_error"] = str(e)

    try:
        simbad_result = Simbad.query_region(coord, radius=error_arcmin * u.arcmin)
        result["known_sources_in_field"] = len(simbad_result) if simbad_result else 0
    except Exception as e:
        result["simbad_error"] = str(e)

    # ── Photometry on real FITS files ─────────────────────────────────────────
    fits_files = find_fits_files(fits_dir, capture_time) if fits_dir else []
    print(f"[Analyzer] Found {len(fits_files)} FITS file(s) for {grb_name}")

    magnitudes = []
    phot_notes = []
    for fpath in fits_files:
        fname = os.path.basename(fpath)
        try:
            mag, note = measure_magnitude(fpath, ra, dec, error_arcmin)
            phot_notes.append(f"{fname}: {note}")
            if mag is not None:
                magnitudes.append(mag)
                print(f"[Analyzer]   {fname} → mag={mag:.2f} | {note}")
            else:
                print(f"[Analyzer]   {fname} → no mag | {note}")
        except Exception as e:
            phot_notes.append(f"{fname}: error — {e}")
            print(f"[Analyzer]   {fname} → error: {e}")

    if magnitudes:
        result["magnitude_mean"]  = round(float(np.mean(magnitudes)), 2)
        result["magnitude_min"]   = round(float(np.min(magnitudes)), 2)
        result["magnitude_max"]   = round(float(np.max(magnitudes)), 2)
        result["magnitude_count"] = len(magnitudes)
        print(f"[Analyzer] Magnitude: {result['magnitude_mean']:.2f} "
              f"(from {len(magnitudes)} frame(s))")
    else:
        result["magnitude_mean"] = None
        if not fits_files:
            result["photometry_note"] = (
                "No FITS files found near capture time. "
                "Check fits_directory path or NINA save folder."
            )

    result["photometry_details"] = phot_notes[:10]  # cap to avoid Firestore size limits

    doc_ref.update(result)
    print(f"[Analyzer] Done: {grb_name} | "
          f"mag={result.get('magnitude_mean')} | "
          f"candidate={result.get('source_candidate')}")


# ── Polling loop ──────────────────────────────────────────────────────────────

def watch_captures():
    print(f"[Analyzer] Watching grb_captures (polling every {POLL_INTERVAL_SECONDS}s)...")
    while True:
        try:
            pending = (
                db.collection("grb_captures")
                  .where(filter=FieldFilter("status", "==", "pending_analysis"))
                  .stream()
            )
            for doc in pending:
                analyze_capture(doc.reference, doc.to_dict())
        except Exception as e:
            print(f"[Analyzer] Firestore query error: {e}")

        time.sleep(POLL_INTERVAL_SECONDS)


if __name__ == "__main__":
    watch_captures()
