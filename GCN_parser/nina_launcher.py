"""
nina_launcher.py

Automatically launches NINA with the GRB nightly sequence 5 minutes before
sunset at the observatory location.

How it works
------------
1. Reads observatory lat/lon from config below (matches NINA profile settings)
2. Calculates today's sunset time using the `astral` library
3. Sleeps until (sunset - MINUTES_BEFORE_SUNSET)
4. Launches NINA.exe with the saved sequence file
5. NINA opens, loads the sequence, and starts the GRBAlertTrigger loop

Setup
-----
1. pip install astral
2. Edit OBSERVATORY_* and path constants below to match your setup
3. Create a Windows Task Scheduler task:
      Trigger  : At system startup  (runs once, then sleeps internally)
      Program  : pythonw.exe
      Arguments: "C:\\path\\to\\GCN_parser\\nina_launcher.py"
      Run as   : your Windows user account (needs desktop access for NINA)

   OR create a second task triggered at 10:00 AM daily as a safety restart
   in case the PC rebooted after sunrise.

Requirements
------------
    pip install astral
"""

import os
import subprocess
import sys
import time
import logging
from datetime import datetime, timezone, timedelta

from astral import LocationInfo
from astral.sun import sun

# ── Observatory configuration ─────────────────────────────────────────────────
# Set these to match your observatory's actual coordinates and timezone.
# These should match NINA Options → Astrometry → Location.
OBSERVATORY_LAT       = 30.0444       # decimal degrees North  (negative = South)
OBSERVATORY_LON       = 31.2357       # decimal degrees East   (negative = West)
OBSERVATORY_ELEVATION = 75            # metres above sea level (affects refraction)
OBSERVATORY_TZ        = "Africa/Cairo"  # IANA timezone string — see:
                                        # https://en.wikipedia.org/wiki/List_of_tz_database_time_zones

# ── Timing ────────────────────────────────────────────────────────────────────
MINUTES_BEFORE_SUNSET = 5             # launch NINA this many minutes before sunset

# ── NINA paths ────────────────────────────────────────────────────────────────
NINA_EXE = r"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe"

# Path to the Advanced Sequencer .json file you saved in NINA.
# Save your sequence in NINA first: Sequence → Save As → choose this path.
SEQUENCE_FILE = r"C:\GRB\grb_nightly.json"

# ── Logging ───────────────────────────────────────────────────────────────────
_HERE    = os.path.dirname(os.path.abspath(__file__))
_LOG     = os.path.join(_HERE, "nina_launcher.log")
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(message)s",
    handlers=[
        logging.FileHandler(_LOG, encoding="utf-8"),
        logging.StreamHandler(sys.stdout),
    ]
)
log = logging.getLogger("nina_launcher")


def get_sunset_utc(date=None):
    """Return today's sunset as a timezone-aware UTC datetime."""
    location = LocationInfo(
        name="Observatory",
        region="",
        timezone=OBSERVATORY_TZ,
        latitude=OBSERVATORY_LAT,
        longitude=OBSERVATORY_LON,
    )
    if date is None:
        date = datetime.now().date()

    s = sun(location.observer, date=date, tzinfo=location.timezone)
    return s["sunset"].astimezone(timezone.utc)


def launch_nina():
    """Start NINA with the pre-configured sequence file."""
    if not os.path.exists(NINA_EXE):
        log.error(f"NINA executable not found: {NINA_EXE}")
        log.error("Update NINA_EXE in nina_launcher.py to match your installation path.")
        sys.exit(1)

    if not os.path.exists(SEQUENCE_FILE):
        log.error(f"Sequence file not found: {SEQUENCE_FILE}")
        log.error(
            "Open NINA, build your GRB sequence in the Advanced Sequencer, "
            f"then save it to:\n  {SEQUENCE_FILE}"
        )
        sys.exit(1)

    log.info(f"Launching NINA: {NINA_EXE}")
    log.info(f"Sequence file : {SEQUENCE_FILE}")

    # /sequenceFile  — tells NINA which .json sequence to load on startup
    # /runSequence   — tells NINA to start running the sequence immediately
    subprocess.Popen(
        [NINA_EXE, "/sequenceFile", SEQUENCE_FILE, "/runSequence"],
        creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    )
    log.info("NINA launched successfully.")


def main():
    log.info("=" * 60)
    log.info("GRB Observatory Launcher started")
    log.info(f"Observatory : lat={OBSERVATORY_LAT}  lon={OBSERVATORY_LON}  tz={OBSERVATORY_TZ}")
    log.info(f"Sequence    : {SEQUENCE_FILE}")

    now_utc    = datetime.now(timezone.utc)
    sunset_utc = get_sunset_utc()
    launch_utc = sunset_utc - timedelta(minutes=MINUTES_BEFORE_SUNSET)

    log.info(f"Today's sunset : {sunset_utc.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    log.info(f"Planned launch : {launch_utc.strftime('%Y-%m-%d %H:%M:%S UTC')} "
             f"({MINUTES_BEFORE_SUNSET} min before sunset)")

    wait_seconds = (launch_utc - now_utc).total_seconds()

    if wait_seconds <= 0:
        log.warning("Launch time has already passed today — launching NINA now.")
        launch_nina()
        return

    hours, rem    = divmod(int(wait_seconds), 3600)
    minutes, secs = divmod(rem, 60)
    log.info(f"Sleeping for {hours}h {minutes}m {secs}s until launch time…")

    # Sleep in 60-second chunks so we can log progress and handle date rollover
    while True:
        now_utc      = datetime.now(timezone.utc)
        remaining    = (launch_utc - now_utc).total_seconds()

        if remaining <= 0:
            break

        if remaining > 3600:
            # More than an hour away — log every hour
            time.sleep(min(3600, remaining))
            h = int((launch_utc - datetime.now(timezone.utc)).total_seconds() // 3600)
            log.info(f"Still waiting… ~{h}h until sunset launch.")
        elif remaining > 60:
            time.sleep(60)
        else:
            time.sleep(remaining)
            break

    log.info("Launch time reached!")
    launch_nina()


if __name__ == "__main__":
    main()
