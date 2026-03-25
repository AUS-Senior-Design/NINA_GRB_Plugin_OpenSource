# %% [markdown]
# # Imports

# %%
from pydantic import BaseModel
from typing import Optional, List
import subprocess
import json
import re
from datetime import datetime, timedelta
import gspread
from oauth2client.service_account import ServiceAccountCredentials
import os
from openai import OpenAI
from googleapiclient.discovery import build
import pandas as pd
from dotenv import load_dotenv
import math
import re
import urllib.request
import json
from firebase_client import push_grb_to_firestore, get_firestore_client, get_grb_by_field

load_dotenv()


# =======================
# OpenAI Client
# =======================
if not os.getenv("OPENAI_API_KEY"):
    raise RuntimeError("OPENAI_API_KEY environment variable not set")

client = OpenAI(api_key=os.getenv("OPENAI_API_KEY"))

# %% [markdown]
# # LLM

# %% [markdown]
# ## Prompts

# %%
SYSTEM_PROMPT = """
You are a scientific information extraction assistant specialized in GCN Circular emails.
Your task is to extract structured observational data ONLY from the provided text.
Do NOT infer, guess, or calculate values beyond explicitly allowed unit conversions.
If a field is not explicitly stated, return NULL.
Output must be valid JSON and nothing else.

You must output ONLY valid JSON.
Do not include explanations, markdown, code fences, or any text outside JSON.
The response MUST begin with '{' and end with '}'.
If you cannot extract a field, use null.
"""

USER_PROMPT_TEMPLATE = """
Extract the following fields from the GCN Circular email below.

Rules:
- Use ONLY information explicitly present in the text.
- Do NOT infer missing values.
- Do NOT compute astrophysical quantities
- The ONLY calculations allowed are unit conversions specified below.
- If a value is missing or ambiguous, return NULL.
- If multiple values exist, choose the most relevant to the event trigger.
- Output JSON only, no commentary.

Expected input units: 
- uncertainty/ error -> arcmin or deg or arcsec
- Flux →  exactly** "erg cm^-2 s^-1" , if the unit is not complete, flux is null
- RA → "hms" or "daa" or "deg".
- Dec → "hms" or "daa" or "deg".
-fluence -> "erg cm^-2"

Fields to extract:

{{
  "GRB_name": null or string,
  "GCN_number": null or number, #this is the "NUMBER: " field under "TITLE" in the email header
  "email_date": date or null ,
  "email_time": time or null,
  "ra": null or string,
  "ra_unit": null or string,
  "dec": null or string,
  "dec_unit": null or string
  "error": null or number,
  "error_unit": string or null,
  "altitude_degrees": null or number,
  "space_telescope": null or one of ["SWIFT", "EP", "SVOM", "FERMI", "OTHERS"],
  "trigger_date": date or null,
  "trigger_time_utc": time or null,
  "obs_start_time_utc" : time or null,
  "obs_end_time_utc": time or null,
  "trigger_offset_value": time or null,
  "trigger_offset_unit": time or null,
  "trigger_offset_relation": time or null,
  "peak_count_per_sec": null or number,
  "flux": null or number,
  "flux_unit" : string or null, 
  "fluence" : string or null,
  "fluence_unit": string or null,
  "snr": null or number,
  "magnitude": null or number
}}

Important extraction notes:
The GCN_number, GRB_name, email_date, and email_time are in the header. DO NOT CHANGE the email_date format, keep it AS IT IS
DO NOT ADD GMT in the email time
The email date and time are NOT the trigger time
The trigger date can be formatted as date
- RA/Dec may appear as hh:mm:ss, degrees, or J2000 coordinates — preserve the first encountered original format.
-Task: Given text containing RA: and/or Dec: values, identify the ra_unit and dec_unit.
Input:
Plain text. Ignore labels like (J2000.0).
Output:
Return only JSON, with keys when present:
ra_unit
dec_unit
Allowed values (strings only):
"deg" — decimal degrees OR
"hms" — hours-minutes-seconds  OR
"daa" — degrees-arcminutes-arcseconds 

RA unit and Dec unit Rules:
"hms" if it contains h m s or colon-separated time (hh:mm:ss); 
"daa" if it contains d ' " or colon-separated angles (dd:mm:ss); 
else "deg".

Example:

RA: 06:38:28.55
Dec: -13d 46' 27.4"
{{"ra_unit": "hms",
"dec_unit":"daa"
}}

- The Error may have a different name like uncertainty
- The Error unit may be arcsec, arcmin, or deg


- ONLY extract flux if the unit **exactly** matches "erg cm^-2 s^-1" (including s^-1 for per second).  
- Do NOT infer flux from any other unit (e.g., "erg cm^-2" or "ph/s/cm^2").  
- If "erg cm^-2 s^-1" is not present in the text, set flux = null and flux_unit = null.
Do NOT convert fluence or other energy quantities into flux. 
Only report a value if the text explicitly gives it in "erg cm^-2 s^-1".
Only consider numbers that are **immediately next to the exact string** "erg cm^-2 s^-1".  
Ignore all other numbers, including "erg cm^-2" or "ph/s/cm^2".



  
Extraction Rules for Fluence:

1. ONLY extract numeric values **explicitly associated** with one of the valid fluence units:
   - Energy fluence: "erg cm^-2", "J m^-2", "keV cm^-2"
   - Photon/particle fluence: "ph cm^-2", "counts cm^-2"
   - Differential fluence (optional): "erg cm^-2 keV^-1", "ph cm^-2 keV^-1"

2. **Ignore uncertainties or error ranges** (e.g., "± 0.1") — only extract the main numeric value.  
   - Example: from "3.2 ± 0.1 erg/cm²", extract `3.2`.

3. ONLY extract numeric values **with the exact unit**.  
   - Do not infer from flux units or per-second quantities.  
   - If the correct unit is not present, set:
     ```
     fluence = null
     fluence_unit = null
     ```

Trigger Time Extraction Policy (CRITICAL):

- If an explicit trigger time or trigger date+time is stated in the text
  (e.g., "triggered at 04:11 UTC", "T0 = 13:12:10.83 UT"),
  extract trigger_date and trigger_time_utc directly.

- Do NOT compute trigger_time_utc from relative offsets.

- If the trigger time is NOT explicitly stated, extract:
  - obs_start_time_utc (earliest observation time)
  - obs_end_time_utc (latest observation time, if present)
  - trigger_offset_value (earliest offset value only)
  - trigger_offset_unit ("minutes" or "hours")
  - trigger_offset_relation ("after_trigger")

- In the relative-offset case, set:
  trigger_date = null
  trigger_time_utc = null
  
  Relative Trigger Offset Rules:

Treat the following phrases as valid trigger offsets:
- "T+X minutes after the trigger"
- "X minutes after the trigger"
- "T+X hours after the trigger"
- "X hours after the trigger"
- "from X to Y minutes after the trigger"
- "from X to Y hours after the trigger"

If a range is given, extract the earliest value (X).

Exclusivity Rule:

If trigger_time_utc is extracted, ALL of the following MUST be null:
- obs_start_time_utc
- obs_end_time_utc
- trigger_offset_value
- trigger_offset_unit
- trigger_offset_relation     

- Altitude should ONLY be extracted if explicitly stated as altitude or elevation.
- Space_Telescope:
  - SWIFT → Swift, Swift-XRT, BAT, UVOT
  - FERMI → Fermi, Fermi-GBM, LAT
  - EP → Einstein Probe
  - SVOM → SVOM, GRM, ECLAIRs
  - OTHERS → IceCube, GECAM, MAXI, or anything else
- Optical magnitude includes detections or upper limits.
- If multiple optical magnitudes exist, return the most constraining (deepest).
- Do NOT average or combine values.

GCN Circular email text:
<<<
{text}
>>>
"""

# Tweaked Prompt
USER_PROMPT_TEMPLATE_2 = """
Extract the following fields from the GCN Circular email below.

Rules:
- Use ONLY information explicitly present in the text.
- Do NOT infer missing values.
- Do NOT compute astrophysical quantities
- The ONLY calculations allowed are unit conversions specified below.
- If a value is missing or ambiguous, return NULL.
- If multiple values exist, choose the most relevant to the event trigger.
- Output JSON only, no commentary.

Expected input units: 
- uncertainty/ error -> arcmin or deg or arcsec
- Flux →  exactly** "erg cm^-2 s^-1" , if the unit is not complete, flux is null
- RA → "hms" or "daa" or "deg".
- Dec → "hms" or "daa" or "deg".
-fluence -> "erg cm^-2"

Fields to extract:

{{
  "GRB_name": null or string,
  "GCN_number": null or number, #this is the "NUMBER: " field under "TITLE" in the email header
  "email_date": date or null ,
  "email_time": time or null,
  "ra": null or string,
  "ra_unit": null or string,
  "dec": null or string,
  "dec_unit": null or string
  "error": null or number,
  "error_unit": string or null,
  "altitude_degrees": null or number,
  "space_telescope": null or one of ["SWIFT", "EP", "SVOM", "FERMI", "OTHERS"],
  "trigger_date": date or null,
  "trigger_time_utc": time or null,
  "obs_start_time_utc" : time or null,
  "obs_end_time_utc": time or null,
  "trigger_offset_value": time or null,
  "trigger_offset_unit": time or null,
  "trigger_offset_relation": time or null,
  "peak_count_per_sec": null or number,
  "flux": null or number,
  "flux_unit" : string or null, 
  "fluence" : string or null,
  "fluence_unit": string or null,
  "snr": null or number,
  "magnitude": null or number
}}

Important extraction notes:
The GCN_number, GRB_name, email_date, and email_time are in the header. DO NOT CHANGE the email_date format, keep it AS IT IS
DO NOT ADD GMT in the email time
The email date and time are NOT the trigger time
The trigger date can be formatted as date
- RA/Dec may appear as hh:mm:ss, degrees, or J2000 coordinates — preserve the first encountered original format.
-Task: Given text containing RA: and/or Dec: values, identify the ra_unit and dec_unit.
Input:
Plain text. Ignore labels like (J2000.0).
Output:
Return only JSON, with keys when present:
ra_unit
dec_unit
Allowed values (strings only):
"deg" — decimal degrees OR
"hms" — hours-minutes-seconds  OR
"daa" — degrees-arcminutes-arcseconds 

RA unit and Dec unit Rules:
"hms" if it contains h m s or colon-separated time (hh:mm:ss); 
"daa" if it contains d ' " or colon-separated angles (dd:mm:ss); 
else "deg".

Example:

RA: 06:38:28.55
Dec: -13d 46' 27.4"
{{"ra_unit": "hms",
"dec_unit":"daa"
}}

Position Error Extraction Rules (VERY IMPORTANT):

- "error" refers ONLY to positional / localization uncertainty of the GRB in the sky.

- ONLY extract error if it is clearly associated with sky position, such as:
  - "error radius"
  - "position uncertainty"
  - "localization uncertainty"
  - uncertainty attached to RA/Dec coordinates

- The error MUST be an angular quantity describing sky location.

Valid units:
- "arcsec"
- "arcmin"
- "deg"

STRICTLY DO NOT extract error from:
- photometric uncertainties (e.g., "r = 20.63 ± 0.04")
- time-related values (e.g., "T90 = 13 ± 4 s", "180 s exposure")
- flux or fluence uncertainties (e.g., "(7.7 ± 0.9)E-06 erg cm^-2")
- any value with units of seconds, minutes, hours, or magnitudes

- If the uncertainty is NOT explicitly tied to sky position, return:
  error = null
  error_unit = null

- If multiple uncertainties exist, ONLY select the one describing positional uncertainty.


- ONLY extract flux if the unit **exactly** matches "erg cm^-2 s^-1" (including s^-1 for per second).  
- Do NOT infer flux from any other unit (e.g., "erg cm^-2" or "ph/s/cm^2").  
- If "erg cm^-2 s^-1" is not present in the text, set flux = null and flux_unit = null.
Do NOT convert fluence or other energy quantities into flux. 
Only report a value if the text explicitly gives it in "erg cm^-2 s^-1".
Only consider numbers that are **immediately next to the exact string** "erg cm^-2 s^-1".  
Ignore all other numbers, including "erg cm^-2" or "ph/s/cm^2".



  
Extraction Rules for Fluence:

1. ONLY extract numeric values **explicitly associated** with one of the valid fluence units:
   - Energy fluence: "erg cm^-2", "J m^-2", "keV cm^-2"
   - Photon/particle fluence: "ph cm^-2", "counts cm^-2"
   - Differential fluence (optional): "erg cm^-2 keV^-1", "ph cm^-2 keV^-1"

2. **Ignore uncertainties or error ranges** (e.g., "± 0.1") — only extract the main numeric value.  
   - Example: from "3.2 ± 0.1 erg/cm²", extract `3.2`.

3. ONLY extract numeric values **with the exact unit**.  
   - Do not infer from flux units or per-second quantities.  
   - If the correct unit is not present, set:
     ```
     fluence = null
     fluence_unit = null
     ```

Trigger Time Extraction Policy (CRITICAL):

- If an explicit trigger time or trigger date+time is stated in the text
  (e.g., "triggered at 04:11 UTC", "T0 = 13:12:10.83 UT"),
  extract trigger_date and trigger_time_utc directly.

- Do NOT compute trigger_time_utc from relative offsets.

- If the trigger time is NOT explicitly stated, extract:
  - obs_start_time_utc (earliest observation time)
  - obs_end_time_utc (latest observation time, if present)
  - trigger_offset_value (earliest offset value only)
  - trigger_offset_unit ("minutes" or "hours")
  - trigger_offset_relation ("after_trigger")

- In the relative-offset case, set:
  trigger_date = null
  trigger_time_utc = null
  
  Relative Trigger Offset Rules:

Treat the following phrases as valid trigger offsets:
- "T+X minutes after the trigger"
- "X minutes after the trigger"
- "T+X hours after the trigger"
- "X hours after the trigger"
- "from X to Y minutes after the trigger"
- "from X to Y hours after the trigger"

If a range is given, extract the earliest value (X).

Exclusivity Rule:

If trigger_time_utc is extracted, ALL of the following MUST be null:
- obs_start_time_utc
- obs_end_time_utc
- trigger_offset_value
- trigger_offset_unit
- trigger_offset_relation     

- Altitude should ONLY be extracted if explicitly stated as altitude or elevation.
- Space_Telescope:
  - SWIFT → Swift, Swift-XRT, BAT, UVOT
  - FERMI → Fermi, Fermi-GBM, LAT
  - EP → Einstein Probe
  - SVOM → SVOM, GRM, ECLAIRs
  - OTHERS → IceCube, GECAM, MAXI, or anything else
- Optical magnitude includes detections or upper limits.
- If multiple optical magnitudes exist, return the most constraining (deepest).
- Do NOT average or combine values.

GCN Circular email text:
<<<
{text}
>>>

"""
# %% [markdown]
# ## LLM calling functions

# %%
# ===========Ollama Specific Functions============
def call_llama(prompt: str) -> str:
    result = subprocess.run(
        ["ollama", "run", "llama3.1:8b"], input=prompt, text=True, capture_output=True
    )

    print("=== LLM RAW OUTPUT ===")
    print(result.stdout)
    print("=====================")

    return result.stdout


def extract_json(text: str) -> dict:
    # Find the first '{' and the last '}'
    start = text.find("{")
    end = text.rfind("}")

    if start == -1 or end == -1 or end <= start:
        raise ValueError(
            "No JSON object detected in LLM output.\nRaw output was:\n" + text
        )

    json_str = text[start : end + 1]

    try:
        return json.loads(json_str)
    except json.JSONDecodeError as e:
        raise ValueError(
            f"Invalid JSON detected.\nError: {e}\nExtracted JSON:\n{json_str}"
        )


def parse_grb_circular_llama(text: str):
    prompt = SYSTEM_PROMPT + "\n" + USER_PROMPT_TEMPLATE.format(text=text)

    raw_output = call_llama(prompt)

    print(raw_output)
    # data = extract_json(raw_output)

    # # Ensure all expected fields exist
    # for field in GRBData.model_fields:
    #     data.setdefault(field, None)

    # return GRBData(**data)


# ===========GPT Specific Functions============
def call_gpt(prompt: str) -> str:
    response = client.chat.completions.create(
        model="gpt-4o-mini",
        messages=[
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ],
        temperature=0.5,
    )
    return response.choices[0].message.content

# Changed Prompt
def parse_grb_circular_gpt(text: str):
    prompt = USER_PROMPT_TEMPLATE_2.format(text=text)
    data = extract_json(call_gpt(prompt))
    # return GRBData(**data)
    return data

# %% [markdown]
# # Utilities

# %% [markdown]
# ## Get date code from GRB name

# %%
# Get date code from the GRB name - e.g. GRB210117A -> 210117
def extract_date_from_grb_name(grb_name: str) -> Optional[str]:
    match = re.search(r"\d{6}", grb_name)
    if match:
        date_str = match.group(0)
        try:
            return date_str
        except ValueError:
            return None
    return None

def extract_grb_name(grb_name: str) -> Optional[str]:
    match = re.search(r"\d{6}[A-Za-z]{1}", grb_name)
    if match:
        date_str = match.group(0)
        try:
            return date_str.lower()
        except ValueError:
            return None
    return None

# %% [markdown]
# ## Unit conversion functions

# %%
# unit conversion functions


def hms_to_deg(hms: str) -> float:
    """
    Convert from hms or hh:mm:ss to degrees.
    """
    hms = hms.strip()

    # 06h 38m 28.55s
    match = re.match(r"(\d+)[h:]\s*(\d+)[m:]\s*([\d.]+)s?", hms)
    if not match:
        raise ValueError(f"Invalid HMS format: {hms}")

    h, m, s = map(float, match.groups())
    return 15.0 * (h + m / 60.0 + s / 3600.0)


def daa_to_deg(daa: str) -> float:
    """
    Convert Declination from degree arcmin arcsec or dd:mm:ss to decimal degrees.
    Accepts formats like:
        -13d 46' 27.4"
        -13:46:27.4
        +13d46'27"
        -29d 00m 58.9s
        +12:30:45
    """
    daa = daa.strip()

    pattern = r"""
        ^\s*
        (?P<deg>[+-]?\d+)          # degrees with optional sign
        \s*(?:d|:)                 # 'd' or colon
        \s*
        (?P<arcmin>\d+)            # arcminutes
        \s*(?:'|m|:)               # ', m, or colon
        \s*
        (?P<arcsec>[\d.]+)         # arcseconds (can be float)
        \s*(?:"|s)?                # optional " or s
        \s*$
    """

    match = re.match(pattern, daa, re.VERBOSE)
    if not match:
        raise ValueError(f"Invalid DAA format: {daa}")

    deg = float(match.group("deg"))
    arcmin = float(match.group("arcmin"))
    arcsec = float(match.group("arcsec"))

    sign = -1 if deg < 0 else 1
    deg = abs(deg)

    return sign * (deg + arcmin / 60.0 + arcsec / 3600.0)


def error_to_deg(value: float, unit: str) -> float:
    if unit == "arcsec":
        return value / 3600.0
    elif unit == "arcmin":
        return value / 60.0
    elif unit == "deg":
        return value
    else:
        raise ValueError(f"Unsupported error unit: {unit}")


def convert_to_deg(llm_output: dict) -> dict:
    out = llm_output.copy()

    # RA
    if out.get("ra") and out.get("ra_unit"):
        if out["ra_unit"] == "hms":
            out["ra_deg"] = hms_to_deg(out["ra"])
        elif out["ra_unit"] == "deg":
            out["ra_deg"] = float(out["ra"])
        else:
            out["ra_deg"] = None
    else:
        out["ra_deg"] = None

    # Dec
    if out.get("dec") and out.get("dec_unit"):
        if out["dec_unit"] == "daa":
            out["dec_deg"] = daa_to_deg(out["dec"])
        elif out["dec_unit"] == "deg":
            out["dec_deg"] = float(out["dec"])
        else:
            out["dec_deg"] = None
    else:
        out["dec_deg"] = None

    # Error
    if out.get("error") is not None and out.get("error_unit"):
        out["error_deg"] = error_to_deg(float(out["error"]), out["error_unit"])
    else:
        out["error_deg"] = None

    return out

# %%
def get_trigger_date(llm_output: dict) -> Optional[str]:
    """
    Determine the trigger date based on the LLM output.
    Trigger date is determined by the GRB name date code (e.g. 210117 from GRB210117A)
    Returns a date string in "YYYY-MM-DD" format or None if it cannot be determined.
    """
    
    grb_date_code = llm_output.get("date_code")  # grb_date_code looks like 260217 for "2026/02/17"
    date_anchor = datetime.strptime(grb_date_code, "%y%m%d") #grb_date_code
    if date_anchor is None:
        return None  # cannot compute without a date
        
    return date_anchor.strftime("%Y-%m-%d")
    

# %%
def parse_trigger_time(trigger_time_utc, date_anchor):
    if trigger_time_utc is None:
        return None

    # 1. Handle the date/time split
    if " " in trigger_time_utc:
        # Use ISO format or strip decimals if they exist
        # dateutil.parser.parse is best here, but manually:
        dt = datetime.fromisoformat(trigger_time_utc.replace("Z", "+00:00"))
    else:
        # 2. Clean the time string for parsing
        # If there's a dot, we need to handle microseconds (%f)
        if "." in trigger_time_utc:
            time_fmt = "%H:%M:%S.%f"
        elif trigger_time_utc.count(":") == 2:
            time_fmt = "%H:%M:%S"
        else:
            time_fmt = "%H:%M"
            
        dt = datetime.strptime(f"{date_anchor} {trigger_time_utc}", f"%Y-%m-%d {time_fmt}")

    # 3. Return formatted string with leading zeros (05 instead of 5)
    return dt.strftime("%H:%M:%S")

# %%
# deterministic trigger time calculation
def compute_trigger_time(llm_output):
    """
    args:
    llm_output: dict containing extracted fields from the LLM output. Should include date_code from extract_date_from_grb_name
    Returns a datetime object for the trigger time in UTC,
    or None if it cannot be computed.
    """

    trigger_time_utc = llm_output.get("trigger_time_utc")  # "04:11:06" or None
    obs_start_time_utc = llm_output.get("obs_start_time_utc")
    trigger_offset_value = llm_output.get("trigger_offset_value")
    trigger_offset_unit = llm_output.get("trigger_offset_unit")

    # -------- Get trigger date --------
    trigger_date = get_trigger_date(llm_output)
    if trigger_date is None:
        return None  # cannot compute without a date
    
    # Case 1: Explicit trigger time exists
    if trigger_time_utc is not None:
        return parse_trigger_time(trigger_time_utc, trigger_date)
    
    # Case 2: Compute from relative offset from obs_start_time
    if (
        obs_start_time_utc is None
        or trigger_offset_value is None
        or trigger_offset_unit is None
    ):
        return None

    obs_fmt = "%H:%M:%S" if obs_start_time_utc.count(":") == 2 else "%H:%M"
    obs_time = datetime.strptime(
        f"{trigger_date} {obs_start_time_utc}", f"%Y-%m-%d {obs_fmt}"
    )

    # Change 1: Added seconds
    if trigger_offset_unit == "seconds":
        delta = timedelta(seconds=float(trigger_offset_value))
    elif trigger_offset_unit == "minutes":
        delta = timedelta(minutes=float(trigger_offset_value))
    elif trigger_offset_unit == "hours":
        delta = timedelta(hours=float(trigger_offset_value))
    elif trigger_offset_unit == "days":
        delta = timedelta(days=float(trigger_offset_value))
        
    else:
        raise ValueError(f"Unsupported offset unit: {trigger_offset_unit}")

    return (obs_time - delta).strftime("%H:%M:%S")

# %% [markdown]
# ## Folder and file creation

# %%
# Convert grb_date_code to datetime
def get_dir_names(llm_output):
    grb_date_code = llm_output["date_code"]  # grb_date_code looks like 260217 for "2026/02/17"

    # Convert to datetime
    dt = datetime.strptime(grb_date_code, "%y%m%d") #grb_date_code

    year_folder = str(dt.year)  # '2026'
    month_folder = f"{dt.month:02d}"  # '02'
    sheet_name = f"{dt.year}_{dt.month:02d}_{dt.day:02d}_grb.csv"  # '2026_02_09_grb'

    return year_folder, month_folder, sheet_name

# %%
BASE_DIR = "GRB_alerts"  # base folder on your desktop


def create_log_folders(BASE_DIR, llm_output):
    year_folder, month_folder, sheet_name = get_dir_names(llm_output)

    year_path = os.path.join(BASE_DIR, year_folder)
    month_path = os.path.join(year_path, month_folder)

    # Create folders if they don't exist
    os.makedirs(month_path, exist_ok=True)

    # Full path for the CSV file
    csv_path = os.path.join(month_path, sheet_name)

    return csv_path

# %%
def save_log(llm_output, base_dir=BASE_DIR):
    """
    Save LLM output to CSV in a deterministic way.
    Every append always goes to a new row.
    """

    # Step 0: Ensure all values are scalars
    llm_output_clean = {
        k: (v[0] if isinstance(v, tuple) else v) for k, v in llm_output.items()
    }

    df_new = pd.DataFrame([llm_output_clean])

    csv_path = create_log_folders(BASE_DIR, llm_output)

    # Ensure parent folder exists
    os.makedirs(os.path.dirname(csv_path), exist_ok=True)

    # Append to CSV
    if os.path.exists(csv_path):
        df_new.to_csv(
            csv_path, mode="a", index=False, header=False, lineterminator="\n"
        )
    else:
        df_new.to_csv(csv_path, mode="w", index=False, header=True, lineterminator="\n")

    print(f"Data written to {csv_path}")

# %%
def save_failed_logs(llm_output, error_message, base_dir=BASE_DIR):
    """
    Save failed LLM outputs to a separate CSV for debugging.
    """
    llm_output_clean = {
        k: (v[0] if isinstance(v, tuple) else v) for k, v in llm_output.items()
    }
    llm_output_clean["error_message"] = error_message

    df_new = pd.DataFrame([llm_output_clean])

    csv_path = os.path.join(base_dir, "failed_logs.csv")

    # Ensure parent folder exists
    os.makedirs(os.path.dirname(csv_path), exist_ok=True)

    # Append to CSV
    if os.path.exists(csv_path):
        df_new.to_csv(
            csv_path, mode="a", index=False, header=False, lineterminator="\n"
        )
    else:
        df_new.to_csv(csv_path, mode="w", index=False, header=True, lineterminator="\n")

    print(f"Failed log written to {csv_path}")

# %% [markdown]
# # Merge alerts

# %%
parsed = {'GRB_name': 'GRB 260223A',
 'email_date': '26/02/23',
 'email_time': '14:15:07',
 'ra': None,
 'ra_unit': None,
 'dec': None,
 'dec_unit': None,
 'error': None,
 'error_unit': None,
 'altitude_degrees': None,
 'space_telescope': 'FERMI',
 'trigger_date': None,
 'trigger_time_utc': '04:11:12',
 'obs_start_time_utc': '06:16',
 'obs_end_time_utc': '07:48',
 'trigger_offset_value': 2.08,
 'trigger_offset_unit': 'hours',
 'trigger_offset_relation': 'after_trigger',
 'peak_count_per_sec': None,
 'flux': None,
 'flux_unit': None,
 'fluence': None,
 'fluence_unit': None,
 'snr': None,
 'magnitude': 18.15,
 'ra_deg': None,
 'dec_deg': None,
 'error_deg': None,
 'date_code': '260223'}

# %%
def update_record(existing_doc_id, existing_dict, new_data):
    updated_data = {**existing_dict}  # copy existing
    for key, value in new_data.items():
        if value is not None:
            updated_data[key] = value

    db = get_firestore_client()
    db.collection("final_grb_alert").document(existing_doc_id).update(updated_data)
    print(f"Updated Firestore record: {existing_doc_id}")

def merge_alert(parsed_raw_alert):
    grb_name = parsed_raw_alert["GRB_name"]
    existing_records = get_grb_by_field("GRB_name", grb_name, collection_name="final_grb_alert")

    if existing_records:
        print(f"GRB: {grb_name} already exists in final_grb_alert.\nMerging and updating.")
        existing = existing_records[0]          # always one record per GRB
        doc_id = existing.pop("id")             # extract the doc ID
        update_record(doc_id, existing, parsed_raw_alert)
    else:
        print(f"GRB: {grb_name} does not exist in final_grb_alert.\nPushing new record.")
        push_grb_to_firestore(parsed_raw_alert, "final_grb_alert")

# %% [markdown]
# # Entry Point Function

# %%
# example test emails

email_text = """TITLE:   GCN CIRCULAR
NUMBER:  43814
SUBJECT: GRB 260223A: COLIBRÍ further optical observations and a brightening
DATE:    26/02/23 14:15:07 GMT
FROM:    Alan Watson at UNAM <alan@astro.unam.mx>

Antonio de Ugarte Postigo (LAM), Leonardo García García (UNAM), Rosa L. Becerra (UNAM), Jean-Grégoire Ducoin (CPPM), Alan M. Watson (UNAM), Stéphane Basa (UAR Pytheas), William H. Lee (UNAM), Jean-Luc Atteia (IRAP), Edilberto Aguilar-Ruiz (UNAM), Dalya Akl (NYUAD), Camila Angulo (UNAM), Sarah Antier (IJCLAB), Nathaniel R. Butler (ASU), Damien Dornic (CPPM), Francis Fortin (IRAP), Ramandeep Gill (UNAM), Noémie Globus (UNAM), Asuka Kuwata (UNAM), Nikos Mandarakas (LAM), Diego López-Cámara (UNAM), Francesco Magnani (CPPM), Enrique Moreno Méndez (UNAM), Margarita Pereyra (UNAM), Ny Avo Rakotondrainibe (LAM), Fredd Sánchez Álvarez (UNAM), and Benjamin Schneider (LAM) report:

We continued to observe the field of the Fermi GRB 260223A (Fermi GBM team et al., GCN Circ. 43808) using the DDRAGO two-channel wide-field imager on the COLIBRÍ telescope. We observed from 2026-02-23 06:16 to 07:48 UTC (from 2.08 to 3.67 hours after the trigger) and obtained near-continuous, alternating 60-second exposures in the grizy filters.

The data were reduced, coadded, calibrated, and analysed with the COLIBRÍ ASU pipeline. The photometry was calibrated using nearby stars from the PanSTARRS DR2, is in the AB system, and is not corrected for Galactic extinction (A_V = 0.68, Schlafly et al. 2011).

The optical counterpart reported by Becerra et al. (GCN Circ. 43811) and confirmed by our earlier observations (de Ugarte Postiga et al. GCN Circ 43812) is observed to brighten continuously during our extended observations, rising from i = 18.55 at the start of our observations to i = 18.15 at the end. Similar behaviour is seen in the other bands.

We thank the staff of the Observatorio Astronómico Nacional on the Sierra de San Pedro Mártir and the COLIBRÍ and DDRAGO engineering teams.

COLIBRÍ is an astronomical observatory developed and operated jointly by France (AMU, CNES and CNRS) and Mexico (UNAM and SECIHTI). It is located at the Observatorio Astronómico Nacional on the Sierra de San Pedro Mártir, Baja California, Mexico.



View this GCN Circular online at https://gcn.nasa.gov/circulars/43814.
---
To unsubscribe, open this link in a web browser:
https://gcn.nasa.gov/unsubscribe/eyJhbGciOiJIUzI1NiJ9.eyJlbWFpbCI6InNlbmlvcmRlc2lnbi4yNTI2QGdtYWlsLmNvbSIsInRvcGljcyI6WyJjaXJjdWxhcnMiXSwiaWF0IjoxNzcxODU2MTEwLCJpc3MiOiJodHRwczovL2djbi5uYXNhLmdvdiJ9.CPaaRn2_gUzB2CjU9pTECwzCvkXt--8TgoskxsN55go

 """

email_text_2 = """TITLE:   GCN CIRCULAR
NUMBER:  43811
SUBJECT: GRB 260223A: DDOTI Optical Candidate
DATE:    26/02/23 05:44:22 GMT
FROM:    Rosa Leticia Becerra Godínez at Instituto de Astronomía,  UNAM <rbecerra@astro.unam.mx>

Rosa L. Becerra (UNAM), Alan M. Watson (UNAM), Camila Angulo Valdez (UNAM), Nat Butler (ASU), Simone Dichiara (Penn State University), Tsvetelina Dimitrova (ASU), Alexander Kutyrev (GSFC/UMD), William H. Lee (UNAM), Océlotl López (UNAM), Margarita Pereyra (UNAM) and Eleonora Troja (U Roma) report:

We observe the field of GRB 260223A (Fermi Team, GCN Circ. 43808) with the DDOTI/OAN wide-field imager at the Observatorio Astronómico Nacional on Sierra San Pedro Mártir (http://ddoti.astroscu.unam.mx) on the night of 2026-02-23 UTC. DDOTI observed from 04:26 UTC to 04:36 UTC (T+14.9 minutes to T+25.3 minutes after the trigger), with a total exposure time of 8 minutes.

Comparing our observations with the USNO-B1 and Pan-STARRS PS1 DR2 catalogues, we detect an uncatalogued fading source at:

RA = 07:24:21.20 (111.0883 deg)
DEC = -29:25:04.4 (-29.4179 deg)

at a preliminary AB magnitude of:

w = 18.30 +/- 0.13

at T + 14.9 minutes. From our observations, we estimate a temporal decay of approximately −0.6. We suggest that this source is the optical counterpart of GRB 260223A and encourage follow-up observations.

We thank the staff of the Observatorio Astronómico Nacional on the Sierra of San Pedro Mártir.



View this GCN Circular online at https://gcn.nasa.gov/circulars/43811.
---
To unsubscribe, open this link in a web browser:"""

# %%
err_email = """ TITLE:   GCN CIRCULAR
NUMBER:  44035
SUBJECT: GRB 260316A: AstroSat CZTI detection
DATE:    26/03/17 09:51:41 GMT
FROM:    Anuraag Arya at IIT Bombay <aryaanuraag910@gmail.com>

Harsha K. H. (IUCAA), M. Tembhurnikar (IUCAA), S. Salunke (IUCAA), A. Arya (IITB), A. Goyal (IITB), G. Waratkar (Caltech/IITB), A. Vibhute (IUCAA), V. Bhalerao (IITB), D. Bhattacharya (Ashoka University/IUCAA), A. R. Rao (IUCAA/TIFR), and S. Vadawale (PRL) report on behalf of the AstroSat CZTI collaboration:

Analysis of AstroSat CZTI data with the CIFT framework (Sharma et al., 2021, JApA, 42, 73) showed the detection of a GRB 260316A which was also detected by Fermi GBM (Fermi GBM Team, GCN Circ. 44031), Calet (Trigger Num. 1457716347), and SVOM (Trigger Num. sb26031602).

The source was clearly detected in the CZT detectors in the 20-200 keV energy range. The light curve peaks at 2026-03-16 17:12:53.75 UTC. The measured peak count rate associated with the burst is 524 (+158, -53) counts/s above the background in the combined data of all quadrants, with a total of 818 (+139, -151) counts. The local mean background count rate was 306 (+5, -8) counts/s. Using cumulative rates, we measure a T90 of 4.91 (+1.7, -2.5) s. 

The source was also faintly detected in the CsI anticoincidence (Veto) detector in the 100-500 keV energy range.

CZTI is built by a TIFR-led consortium of institutes across India, including VSSC, URSC, IUCAA, SAC, and PRL. The Indian Space Research Organisation funded, managed, and facilitated the project.

CZTI GRB detections are reported regularly on the payload site at:
http://astrosat.iucaa.in/czti/?q=grb



View this GCN Circular online at https://gcn.nasa.gov/circulars/44035.
---
To unsubscribe, open this link in a web browser:
https://gcn.nasa.gov/unsubscribe/eyJhbGciOiJIUzI1NiJ9.eyJlbWFpbCI6InNlbmlvcmRlc2lnbi4yNTI2QGdtYWlsLmNvbSIsInRvcGljcyI6WyJjaXJjdWxhcnMiXSwiaWF0IjoxNzczNzQxMTA1LCJpc3MiOiJodHRwczovL2djbi5uYXNhLmdvdiJ9.COQjGD-sWg-rF2mIBRJRB8XZv6z68ulv0m767EDGWBY
"""

# %%
def get_llm_output(email_text):
    parsed_raw = None
    try:
        # llm_output = parse_grb_circular_llama(email_text)
        parsed_raw = parse_grb_circular_gpt(email_text)
        parsed = convert_to_deg(parsed_raw)
        parsed["date_code"] = extract_date_from_grb_name(parsed.get("GRB_name", ""))
        parsed["GRB_name"] = extract_grb_name(parsed.get("GRB_name", ""))
        parsed["trigger_time_utc"] = compute_trigger_time(parsed)
        return parsed
    except Exception as e:
        print(f"[ERROR] Failed to process email: {e}")
        if parsed_raw is not None:
            save_failed_logs(parsed_raw, str(e))
        return None

# %%
# saves any raw alert we get , hopefully pushes to firestore
# update this to also get final_alert

def parse_email_text(email_text: str):
    """
    Entry point from Gmail listener.
    Receives raw email body as plain text.
    """

    print("\n================ RAW EMAIL TEXT =================")
    print(email_text.strip())
    print("=================================================\n")

    parsed = get_llm_output(email_text)
    print(f"===== PARSED OUTPUT========\n{parsed}\n")
    save_log(parsed)
    merge_alert(parsed)



