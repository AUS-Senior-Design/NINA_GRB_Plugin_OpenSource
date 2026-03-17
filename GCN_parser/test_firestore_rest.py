# Tests the same Firestore REST API call that FirestoreGrbListener.cs makes.
import json
import requests
from google.oauth2 import service_account
from google.auth.transport.requests import Request

SERVICE_ACCOUNT_PATH = "firebase_service_account.json"
SCOPES = ["https://www.googleapis.com/auth/datastore"]

# Load service account and get token (same as C# GoogleCredential)
creds = service_account.Credentials.from_service_account_file(SERVICE_ACCOUNT_PATH, scopes=SCOPES)
creds.refresh(Request())
token = creds.token

# Read project ID from service account
with open(SERVICE_ACCOUNT_PATH) as f:
    sa = json.load(f)
project_id = sa["project_id"]

# Same URL the C# code hits
url = f"https://firestore.googleapis.com/v1/projects/{project_id}/databases/(default)/documents/grb_alerts"
headers = {"Authorization": f"Bearer {token}"}

print(f"Project: {project_id}")
print(f"Querying: {url}\n")

response = requests.get(url, headers=headers)
print(f"HTTP Status: {response.status_code}")

if response.status_code != 200:
    print("ERROR:", response.text)
else:
    data = response.json()
    docs = data.get("documents", [])
    print(f"Documents found: {len(docs)}")
    for doc in docs:
        name = doc.get("name", "")
        fields = doc.get("fields", {})
        grb_name = fields.get("name", {}).get("stringValue", "N/A")
        trigger = fields.get("trigger_time", {}).get("stringValue", "N/A")
        dec = fields.get("dec", {}).get("doubleValue", fields.get("dec", {}).get("integerValue", "N/A"))
        mag = fields.get("magnitude", {}).get("doubleValue", fields.get("magnitude", {}).get("integerValue", "N/A"))
        print(f"  Doc ID: {name.split('/')[-1]}")
        print(f"  GRB name: {grb_name}")
        print(f"  trigger_time: {trigger}")
        print(f"  dec: {dec}  mag: {mag}")
        print()
