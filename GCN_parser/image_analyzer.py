"""
image_analyzer.py

Watches Firestore grb_captures collection for status == "pending_analysis".
For each capture record:
  1. Finds the FITS files saved by NINA around the capture time
  2. Runs FORCED aperture photometry at the GRB position in every frame
  3. Computes SNR at the GRB position even when no source is detected
  4. Reports calibrated magnitude + uncertainty for detections (SNR >= 3)
  5. Reports a 3-sigma upper limit for non-detections (SNR < 3)
  6. Calibrates against Gaia DR3 reference stars (zero-point method)
  7. Fits a power-law temporal decay F(t) ∝ t^(-α) across all frames
  8. Writes the full structured light curve and decay index back to Firestore

Scientific methodology:
  - Kumar et al. (2025), MNRAS 544, 1541         → aperture photometry, 3σ upper limits
  - Cheng et al. (2025), ApJ 979, 38             → Gaia calibration, photometric uncertainties
  - Del Vecchio et al. (2016), ApJ 828, 36       → power-law decay index α
  - Lyman et al. (2026), arXiv:2603.02330        → source detection pipeline (DAOStarFinder)

Requirements:
    pip install firebase-admin astroquery astropy numpy photutils scipy rawpy
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
    from scipy.optimize import curve_fit
    HAS_SCIPY = True
except ImportError:
    HAS_SCIPY = False
    print("[Analyzer] Warning: scipy not installed — power-law decay fitting disabled. Run: pip install scipy")

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
from photutils.aperture import CircularAperture, CircularAnnulus, aperture_photometry

# ── Firebase init ─────────────────────────────────────────────────────────────
_HERE   = os.path.dirname(os.path.abspath(__file__))
_SA_PATH = os.path.join(_HERE, "firebase_service_account.json")

if not os.path.exists(_SA_PATH):
    raise FileNotFoundError(
        f"[Analyzer] Service account file not found at:\n  {_SA_PATH}\n"
        "Place firebase_service_account.json in the GCN_parser/ folder."
    )

cred = credentials.Certificate(_SA_PATH)
firebase_admin.initialize_app(cred)
db = firestore.client()

# ── Constants ─────────────────────────────────────────────────────────────────
POLL_INTERVAL_SECONDS = 30
APERTURE_RADIUS_PX    = 8.0    # aperture radius in pixels (photometry aperture)
ANNULUS_INNER_PX      = 10.0   # inner radius of background annulus (pixels)
ANNULUS_OUTER_PX      = 15.0   # outer radius of background annulus (pixels)
DETECTION_SIGMA       = 3.0    # SNR threshold for a detection (standard in GRB literature)
SEARCH_WINDOW_MIN     = 15     # look for FITS files within ±15 min of capture time
MIN_FRAMES_FOR_FIT    = 3      # minimum detections needed to attempt power-law fit
MIN_TIME_SPREAD_HOURS = 0.05   # minimum time spread (3 min) to attempt power-law fit
Gaia.ROW_LIMIT        = 50


# ─────────────────────────────────────────────────────────────────────────────
# STEP 1 — FITS FILE DISCOVERY
# ─────────────────────────────────────────────────────────────────────────────

def find_fits_files(fits_directory, capture_time_str):
    """
    Return FITS files in fits_directory saved within SEARCH_WINDOW_MIN of capture_time.
    Supports FITS, XISF, and Canon RAW formats.
    """
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


# ─────────────────────────────────────────────────────────────────────────────
# STEP 2 — IMAGE LOADER
# ─────────────────────────────────────────────────────────────────────────────

def load_image(path):
    """Load image data and header from FITS or Canon RAW file."""
    ext = os.path.splitext(path)[1].lower()
    if ext in ('.cr2', '.cr3'):
        if not HAS_RAWPY:
            raise RuntimeError("rawpy not installed — cannot read RAW files")
        with rawpy.imread(path) as raw:
            data = raw.raw_image_visible.astype(float)
        return data, {}
    else:
        with fits.open(path) as hdul:
            return hdul[0].data.astype(float), hdul[0].header


# ─────────────────────────────────────────────────────────────────────────────
# STEP 3 — OBSERVATION TIME EXTRACTION
# ─────────────────────────────────────────────────────────────────────────────

def get_obs_time(fits_path, header):
    """
    Extract the actual observation timestamp from the FITS header (DATE-OBS),
    falling back to the file modification time if the header field is missing.

    Returns a timezone-aware UTC datetime.
    """
    # Try FITS standard DATE-OBS keyword first
    date_obs = header.get("DATE-OBS") or header.get("DATE_OBS") or header.get("DATE")
    if date_obs:
        try:
            # FITS DATE-OBS can be "YYYY-MM-DDThh:mm:ss.sss" or "YYYY-MM-DD"
            dt = datetime.fromisoformat(str(date_obs).replace("Z", "+00:00"))
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
        except Exception:
            pass

    # Fall back to file modification time
    return datetime.fromtimestamp(os.path.getmtime(fits_path), tz=timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# STEP 4 — 3σ UPPER LIMIT
# Per Kumar et al. (2025), MNRAS 544, 1541
# ─────────────────────────────────────────────────────────────────────────────

def compute_upper_limit(bkg_rms, aperture_r_px, zero_point):
    """
    Compute the 3-sigma limiting magnitude at the GRB position.

    When no source is detected (SNR < 3), we do not report "nothing".
    Instead we report the faintest source we COULD have detected.
    This is standard practice in GRB GCN Circulars and papers
    (Kumar et al. 2025, Lyman et al. 2026).

    Formula (from Kumar et al. 2025):
        noise      = bkg_rms × √(π × r²)        [total noise in aperture]
        flux_limit = 3 × noise                   [3σ detection threshold]
        m_lim      = ZP − 2.5 × log10(flux_limit)

    Parameters
    ----------
    bkg_rms      : float  — per-pixel background RMS (from sigma-clipped stats)
    aperture_r_px: float  — aperture radius in pixels
    zero_point   : float  — photometric zero point from Gaia calibration

    Returns
    -------
    float — limiting magnitude, or None if zero_point is unavailable
    """
    if zero_point is None:
        return None
    aperture_area = np.pi * aperture_r_px ** 2
    noise         = bkg_rms * np.sqrt(aperture_area)
    flux_limit    = DETECTION_SIGMA * noise
    if flux_limit <= 0:
        return None
    return round(zero_point - 2.5 * np.log10(flux_limit), 2)


# ─────────────────────────────────────────────────────────────────────────────
# STEP 5 — CORE PHOTOMETRY
# Forced aperture photometry + Gaia calibration
# Per Kumar et al. (2025) and Cheng et al. (2025)
# ─────────────────────────────────────────────────────────────────────────────

def measure_magnitude(fits_path, ra, dec, error_arcmin, trigger_time=None):
    """
    Perform forced aperture photometry at the GRB coordinates and calibrate
    against Gaia DR3 reference stars.

    KEY DIFFERENCE from the old approach:
    - OLD: only measured where DAOStarFinder found a source → silent None on non-detection
    - NEW: ALWAYS measures at the GRB position (forced photometry), computes SNR,
           and returns either a calibrated magnitude or a 3σ upper limit.
           This matches the standard used by Kumar et al. (2025) and Lyman et al. (2026).

    Returns a dict:
    {
        "magnitude"    : float or None   — calibrated mag (if SNR >= 3)
        "mag_error"    : float or None   — photometric uncertainty (Poisson + ZP scatter)
        "snr"          : float           — measured SNR at GRB position
        "upper_limit"  : float or None   — 3σ limiting mag (if SNR < 3)
        "is_detection" : bool            — True if SNR >= DETECTION_SIGMA (3)
        "zero_point"   : float or None   — Gaia-calibrated zero point
        "zp_scatter"   : float or None   — scatter in ZP across reference stars (mag)
        "n_ref_stars"  : int             — number of Gaia reference stars used
        "delta_t_hours": float or None   — time since GRB trigger (hours)
        "obs_time"     : str or None     — ISO timestamp of this frame
        "note"         : str             — human-readable summary
    }
    """
    result = {
        "magnitude": None, "mag_error": None, "snr": 0.0,
        "upper_limit": None, "is_detection": False,
        "zero_point": None, "zp_scatter": None, "n_ref_stars": 0,
        "delta_t_hours": None, "obs_time": None, "note": ""
    }

    data, header = load_image(fits_path)

    # ── Observation time + Δt since trigger ──────────────────────────────────
    obs_time = get_obs_time(fits_path, header)
    result["obs_time"] = obs_time.isoformat()
    if trigger_time is not None:
        try:
            if isinstance(trigger_time, str):
                t0 = datetime.fromisoformat(trigger_time.replace("Z", "+00:00"))
                if t0.tzinfo is None:
                    t0 = t0.replace(tzinfo=timezone.utc)
            else:
                t0 = trigger_time
            delta_seconds = (obs_time - t0).total_seconds()
            result["delta_t_hours"] = round(delta_seconds / 3600.0, 6)
        except Exception:
            pass

    # ── Background subtraction ────────────────────────────────────────────────
    # sigma_clipped_stats removes bright stars and cosmic rays before computing
    # the background level (median) and its noise (std / bkg_rms).
    # bkg_rms is the per-pixel noise floor — critical for SNR and upper limits.
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        _, median, bkg_rms = sigma_clipped_stats(data, sigma=3.0)
    data_sub = data - median

    if bkg_rms <= 0:
        result["note"] = "Background RMS is zero — image may be blank"
        return result

    # ── WCS: locate GRB in pixel coordinates ─────────────────────────────────
    has_wcs = False
    grb_x = grb_y = None
    grb_coord = SkyCoord(ra * u.deg, dec * u.deg)

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", FITSFixedWarning)
            wcs = WCS(header)
        if wcs.has_celestial:
            grb_x, grb_y = wcs.world_to_pixel(grb_coord)
            has_wcs = True
    except Exception:
        pass

    if not has_wcs:
        # NINA pointed at the GRB, so it should be near image centre
        grb_x, grb_y = data.shape[1] / 2.0, data.shape[0] / 2.0

    # Clip to valid pixel range
    grb_x = float(np.clip(grb_x, APERTURE_RADIUS_PX, data.shape[1] - APERTURE_RADIUS_PX))
    grb_y = float(np.clip(grb_y, APERTURE_RADIUS_PX, data.shape[0] - APERTURE_RADIUS_PX))

    # ── FORCED aperture photometry at GRB position ────────────────────────────
    # We measure the flux at the GRB position regardless of whether DAOStarFinder
    # detected a source there. This is "forced photometry" — the standard approach
    # for transient follow-up (Kumar et al. 2025, Lyman et al. 2026).
    aperture      = CircularAperture([(grb_x, grb_y)], r=APERTURE_RADIUS_PX)
    phot_table    = aperture_photometry(data_sub, aperture)
    grb_counts    = float(phot_table["aperture_sum"][0])

    # SNR = counts / (background noise integrated over aperture area)
    # noise = bkg_rms × √(π × r²)   [Poisson-dominated background noise]
    aperture_area = np.pi * APERTURE_RADIUS_PX ** 2
    noise         = bkg_rms * np.sqrt(aperture_area)
    snr           = grb_counts / noise if noise > 0 else 0.0
    result["snr"] = round(float(snr), 2)

    # ── Gaia calibration (zero-point) ─────────────────────────────────────────
    # We calibrate the photometric zero-point using Gaia DR3 G-band magnitudes
    # of reference stars in the field. Per Cheng et al. (2025):
    #   ZP = ref_mag + 2.5 × log10(ref_counts)   for each reference star
    #   ZP_final = median(ZP values)               robust against outliers
    zero_point = None
    zp_scatter = None
    n_ref      = 0

    if has_wcs and grb_counts > 0:
        try:
            daofind  = DAOStarFinder(fwhm=4.0, threshold=5.0 * bkg_rms)
            sources  = daofind(data_sub)

            gaia_result = Gaia.query_object_async(coordinate=grb_coord, radius=5 * u.arcmin)
            if gaia_result is not None and len(gaia_result) > 0:
                zp_values = []
                for row in gaia_result:
                    g_mag = row["phot_g_mean_mag"]
                    if g_mag is None or np.isnan(float(g_mag)):
                        continue
                    ref_sky = SkyCoord(float(row["ra"]) * u.deg, float(row["dec"]) * u.deg)
                    rx, ry  = wcs.world_to_pixel(ref_sky)
                    if not (10 < rx < data.shape[1] - 10 and 10 < ry < data.shape[0] - 10):
                        continue
                    ref_phot   = aperture_photometry(data_sub, CircularAperture([(rx, ry)], r=APERTURE_RADIUS_PX))
                    ref_counts = float(ref_phot["aperture_sum"][0])
                    if ref_counts > 0:
                        zp_values.append(float(g_mag) + 2.5 * np.log10(ref_counts))

                if len(zp_values) >= 3:
                    zero_point = float(np.median(zp_values))
                    zp_scatter = float(np.std(zp_values))
                    n_ref      = len(zp_values)
                    result["zero_point"]  = round(zero_point, 3)
                    result["zp_scatter"]  = round(zp_scatter, 3)
                    result["n_ref_stars"] = n_ref
        except Exception as e:
            result["note"] += f" | Gaia calibration error: {e}"

    # ── Detection or upper limit ───────────────────────────────────────────────
    if snr >= DETECTION_SIGMA:
        # DETECTION: SNR ≥ 3σ — source confirmed at GRB position
        result["is_detection"] = True

        if zero_point is not None and grb_counts > 0:
            magnitude = zero_point - 2.5 * np.log10(grb_counts)

            # Photometric uncertainty (Cheng et al. 2025):
            #   σ_Poisson = (2.5/ln10) × (1/SNR)   — shot noise contribution
            #   σ_ZP      = zp_scatter               — calibration uncertainty
            #   σ_total   = √(σ_Poisson² + σ_ZP²)
            sigma_poisson = (2.5 / np.log(10)) * (1.0 / snr)
            sigma_zp      = zp_scatter if zp_scatter is not None else 0.0
            mag_error     = float(np.sqrt(sigma_poisson ** 2 + sigma_zp ** 2))

            result["magnitude"]  = round(magnitude, 2)
            result["mag_error"]  = round(mag_error, 3)
            result["note"]       = (f"DETECTION SNR={snr:.1f} | "
                                    f"mag={magnitude:.2f}±{mag_error:.3f} | "
                                    f"ZP={zero_point:.2f} (scatter={zp_scatter:.2f}, "
                                    f"n={n_ref} Gaia stars)")
        else:
            # Detected but no calibration — report instrumental magnitude
            if grb_counts > 0:
                inst_mag        = -2.5 * np.log10(grb_counts)
                result["magnitude"] = round(inst_mag, 2)
                result["note"]  = f"DETECTION SNR={snr:.1f} | instrumental mag (no Gaia WCS)"
            else:
                result["note"]  = f"DETECTION SNR={snr:.1f} | counts non-positive, no magnitude"
    else:
        # NON-DETECTION: SNR < 3σ — report 3σ upper limit instead of nothing
        # Per Kumar et al. (2025): "upper limits are given at the 3σ confidence level"
        upper_limit = compute_upper_limit(bkg_rms, APERTURE_RADIUS_PX, zero_point)
        result["upper_limit"] = upper_limit
        ul_str = f">{upper_limit:.2f}" if upper_limit is not None else ">unknown (no ZP)"
        result["note"] = (f"NON-DETECTION SNR={snr:.1f} < {DETECTION_SIGMA}σ | "
                          f"3σ upper limit: {ul_str} mag")

    return result


# ─────────────────────────────────────────────────────────────────────────────
# STEP 6 — POWER-LAW TEMPORAL DECAY FIT
# Per Del Vecchio et al. (2016), ApJ 828, 36
# ─────────────────────────────────────────────────────────────────────────────

def fit_power_law_decay(light_curve):
    """
    Fit the GRB optical afterglow temporal decay F(t) ∝ t^(-α)
    across all frames where a detection was made.

    In GRB afterglow physics (Del Vecchio et al. 2016), the optical flux
    follows a power law with time after the trigger:
        F(t) = F₀ × t^(-α)

    In magnitude space this becomes:
        m(t) = m₀ + 2.5 × α × log10(t)

    Typical values for optical afterglows: α ≈ 0.8 – 1.5
    (Del Vecchio et al. 2016 survey of 176 Swift GRBs)

    Parameters
    ----------
    light_curve : list of dicts, each with:
        "delta_t_hours" : float  — time since trigger in hours
        "magnitude"     : float  — calibrated magnitude at that time
        "mag_error"     : float  — photometric uncertainty

    Returns
    -------
    dict with:
        "alpha"            : float or None  — temporal decay index
        "alpha_error"      : float or None  — 1σ uncertainty on α
        "m0"               : float or None  — magnitude at t=1 hour (intercept)
        "n_points"         : int            — number of points used in fit
        "fit_time_span_hr" : float          — time baseline of the fit
        "note"             : str
    """
    if not HAS_SCIPY:
        return {"alpha": None, "alpha_error": None, "m0": None,
                "n_points": 0, "fit_time_span_hr": 0.0,
                "note": "scipy not installed — fit skipped"}

    # Only use frames with confirmed detections and valid delta_t
    detections = [
        f for f in light_curve
        if f.get("is_detection") and
           f.get("magnitude") is not None and
           f.get("delta_t_hours") is not None and
           f["delta_t_hours"] > 0
    ]

    if len(detections) < MIN_FRAMES_FOR_FIT:
        return {"alpha": None, "alpha_error": None, "m0": None,
                "n_points": len(detections), "fit_time_span_hr": 0.0,
                "note": f"Only {len(detections)} detections — need {MIN_FRAMES_FOR_FIT} minimum for fit"}

    t    = np.array([f["delta_t_hours"] for f in detections])
    m    = np.array([f["magnitude"]     for f in detections])
    merr = np.array([f.get("mag_error") or 0.1 for f in detections])

    time_span = float(t.max() - t.min())
    if time_span < MIN_TIME_SPREAD_HOURS:
        return {"alpha": None, "alpha_error": None, "m0": None,
                "n_points": len(detections), "fit_time_span_hr": round(time_span, 4),
                "note": f"Time span {time_span*60:.1f} min too short for reliable decay fit"}

    # Fit m(t) = m₀ + 2.5·α·log10(t)  in magnitude space
    # This is a linear fit in log-space: m = m₀ + slope × log10(t)
    # where slope = 2.5·α  →  α = slope / 2.5
    def mag_power_law(log_t, m0, slope):
        return m0 + slope * log_t

    log_t = np.log10(t)

    try:
        popt, pcov = curve_fit(
            mag_power_law, log_t, m, sigma=merr, absolute_sigma=True, maxfev=5000
        )
        perr    = np.sqrt(np.diag(pcov))
        m0      = float(popt[0])
        slope   = float(popt[1])
        alpha   = slope / 2.5
        alpha_e = float(perr[1]) / 2.5

        return {
            "alpha":            round(alpha,   4),
            "alpha_error":      round(alpha_e, 4),
            "m0":               round(m0,      3),
            "n_points":         len(detections),
            "fit_time_span_hr": round(time_span, 4),
            "note": (f"Power-law fit: α={alpha:.3f}±{alpha_e:.3f} "
                     f"over {time_span*60:.1f} min ({len(detections)} points) | "
                     f"Ref: Del Vecchio et al. 2016, ApJ 828, 36")
        }
    except Exception as e:
        return {"alpha": None, "alpha_error": None, "m0": None,
                "n_points": len(detections), "fit_time_span_hr": round(time_span, 4),
                "note": f"Power-law fit failed: {e}"}


# ─────────────────────────────────────────────────────────────────────────────
# STEP 7 — MAIN ANALYSIS ORCHESTRATOR
# ─────────────────────────────────────────────────────────────────────────────

def analyze_capture(doc_ref, data):
    grb_name     = data.get("grb_name", "Unknown")
    ra           = data.get("ra_deg")
    dec          = data.get("dec_deg")
    error_arcmin = data.get("error_arcmin", 5.0)
    fits_dir     = data.get("fits_directory", "")
    capture_time = data.get("capture_time", "")
    trigger_time = data.get("trigger_time")   # added by FirestoreCapturePoster

    if ra is None or dec is None:
        doc_ref.update({"status": "error", "error": "Missing ra_deg or dec_deg"})
        return

    print(f"[Analyzer] Processing {grb_name} — RA={ra:.4f}°, Dec={dec:.4f}°")
    coord  = SkyCoord(ra * u.deg, dec * u.deg)
    result = {"status": "analyzed", "analyzed_at": firestore.SERVER_TIMESTAMP}

    # ── DSS reference check + Simbad known-source lookup ─────────────────────
    try:
        images = SkyView.get_images(position=coord, survey=["DSS"], radius=10 * u.arcmin, pixels=512)
        if images:
            ref_data              = images[0][0].data.astype(float)
            _, ref_median, ref_std = sigma_clipped_stats(ref_data, sigma=3.0)
            cy, cx                = ref_data.shape[0] // 2, ref_data.shape[1] // 2
            box                   = ref_data[cy - 5:cy + 5, cx - 5:cx + 5]
            peak_sigma            = float((np.max(box) - ref_median) / ref_std) if ref_std > 0 else 0.0
            result.update({
                "dss_peak_sigma":   round(peak_sigma, 2),
                "source_candidate": peak_sigma > DETECTION_SIGMA,
            })
    except Exception as e:
        result["dss_error"] = str(e)

    try:
        simbad_result              = Simbad.query_region(coord, radius=error_arcmin * u.arcmin)
        result["known_sources_in_field"] = len(simbad_result) if simbad_result else 0
    except Exception as e:
        result["simbad_error"] = str(e)

    # ── Per-frame photometry ──────────────────────────────────────────────────
    fits_files  = find_fits_files(fits_dir, capture_time) if fits_dir else []
    print(f"[Analyzer] Found {len(fits_files)} FITS file(s) for {grb_name}")

    light_curve  = []   # one entry per frame (detection OR upper limit)
    phot_details = []   # human-readable per-frame notes

    for fpath in fits_files:
        fname = os.path.basename(fpath)
        try:
            frame = measure_magnitude(fpath, ra, dec, error_arcmin, trigger_time)
            frame["filename"] = fname
            light_curve.append(frame)
            phot_details.append(f"{fname}: {frame['note']}")

            if frame["is_detection"]:
                print(f"[Analyzer]   {fname} → DETECTION  mag={frame['magnitude']:.2f}"
                      f"±{frame.get('mag_error',0):.3f}  SNR={frame['snr']:.1f}"
                      f"  Δt={frame.get('delta_t_hours','?')}h")
            else:
                ul = frame.get("upper_limit")
                print(f"[Analyzer]   {fname} → NON-DETECT  UL>={ul}  SNR={frame['snr']:.1f}"
                      f"  Δt={frame.get('delta_t_hours','?')}h")

        except Exception as e:
            phot_details.append(f"{fname}: error — {e}")
            print(f"[Analyzer]   {fname} → ERROR: {e}")

    # ── Summary statistics from light curve ──────────────────────────────────
    detections   = [f for f in light_curve if f["is_detection"] and f["magnitude"] is not None]
    upper_limits = [f for f in light_curve if not f["is_detection"] and f["upper_limit"] is not None]

    if detections:
        mags = [f["magnitude"] for f in detections]
        result.update({
            "magnitude_mean":  round(float(np.mean(mags)), 2),
            "magnitude_min":   round(float(np.min(mags)),  2),
            "magnitude_max":   round(float(np.max(mags)),  2),
            "magnitude_count": len(mags),
        })
        print(f"[Analyzer] Detections: {len(mags)} frames, "
              f"mean mag = {result['magnitude_mean']:.2f}")
    else:
        result["magnitude_mean"] = None
        if not fits_files:
            result["photometry_note"] = (
                "No FITS files found near capture time. "
                "Check fits_directory path or NINA save folder."
            )

    # Deepest upper limit across all non-detection frames
    if upper_limits:
        result["deepest_upper_limit"] = max(f["upper_limit"] for f in upper_limits)

    # ── Power-law decay fit ───────────────────────────────────────────────────
    # Fit F(t) ∝ t^(-α) per Del Vecchio et al. (2016), ApJ 828, 36
    # α is the temporal decay index — the key scientific output of this analysis
    decay_fit = fit_power_law_decay(light_curve)
    result["decay_alpha"]         = decay_fit.get("alpha")
    result["decay_alpha_error"]   = decay_fit.get("alpha_error")
    result["decay_fit_note"]      = decay_fit.get("note")
    result["decay_fit_n_points"]  = decay_fit.get("n_points", 0)

    if decay_fit.get("alpha") is not None:
        print(f"[Analyzer] Power-law decay: α = {decay_fit['alpha']:.3f} "
              f"± {decay_fit['alpha_error']:.3f}")

    # ── Store full light curve in Firestore ───────────────────────────────────
    # Each entry has: filename, obs_time, delta_t_hours, magnitude, mag_error,
    #                 snr, upper_limit, is_detection, zero_point, zp_scatter, note
    # Cap at 20 frames to stay within Firestore document size limits
    result["light_curve"]       = light_curve[:20]
    result["photometry_details"] = phot_details[:10]

    doc_ref.update(result)
    print(f"[Analyzer] Done: {grb_name} | "
          f"detections={len(detections)} | "
          f"upper_limits={len(upper_limits)} | "
          f"α={decay_fit.get('alpha')}")


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
