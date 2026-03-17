import os
import time
import base64
from typing import Optional

from googleapiclient.discovery import build
from google.auth.transport.requests import Request
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import InstalledAppFlow
from parser import parse_email_text

# Needs modify permission to mark as read
SCOPES = ["https://www.googleapis.com/auth/gmail.modify"]

CREDENTIALS_FILE = "credentials.json"
TOKEN_FILE = "token.json"

def handle_email_text(email_text: str):
    """
    This function receives the plain-text body of an email.
    For now: just print it.
    Later: plug into your GRB parser.
    """
    print("\n[EMAIL TEXT RECEIVED AND PARSED]")
    parse_email_text(email_text)

def get_gmail_service():
    creds = None

    if os.path.exists(TOKEN_FILE):
        creds = Credentials.from_authorized_user_file(TOKEN_FILE, SCOPES)

    if not creds or not creds.valid:
        if creds and creds.expired and creds.refresh_token:
            creds.refresh(Request())
        else:
            if not os.path.exists(CREDENTIALS_FILE):
                raise FileNotFoundError(
                    f"Missing {CREDENTIALS_FILE}. Download OAuth client JSON and place it next to this script."
                )
            flow = InstalledAppFlow.from_client_secrets_file(CREDENTIALS_FILE, SCOPES)
            creds = flow.run_local_server(port=0)

        with open(TOKEN_FILE, "w", encoding="utf-8") as f:
            f.write(creds.to_json())

    return build("gmail", "v1", credentials=creds)

def _decode_base64url(data: str) -> str:
    return base64.urlsafe_b64decode(data.encode("utf-8")).decode("utf-8", errors="replace")


def extract_text_from_message(message: dict) -> str:
    """
    Best-effort: returns the first text/plain part found.
    Falls back to snippet if no body found.
    """
    payload = message.get("payload", {}) or {}

    def walk(part: dict) -> Optional[str]:
        mime = part.get("mimeType", "")
        body = part.get("body", {}) or {}

        if mime == "text/plain" and body.get("data"):
            return _decode_base64url(body["data"])

        # multipart: recurse
        for child in (part.get("parts") or []):
            got = walk(child)
            if got:
                return got

        return None

    text = walk(payload)

    if text and text.strip():
        return text

    # Fallback: sometimes body sits directly here
    body_data = (payload.get("body") or {}).get("data")
    if body_data:
        return _decode_base64url(body_data)

    return message.get("snippet", "") or ""


def get_header(message: dict, name: str) -> str:
    headers = (message.get("payload", {}) or {}).get("headers", []) or []
    for h in headers:
        if h.get("name", "").lower() == name.lower():
            return h.get("value", "") or ""
    return ""


def mark_as_read(service, msg_id: str):
    # Remove UNREAD label
    service.users().messages().modify(
        userId="me",
        id=msg_id,
        body={"removeLabelIds": ["UNREAD"]}
    ).execute()


def fetch_unread_inbox_ids(service, max_results: int = 10):
    # unread + inbox
    resp = service.users().messages().list(
        userId="me",
        q="in:inbox is:unread",
        maxResults=max_results
    ).execute()
    return [m["id"] for m in resp.get("messages", [])]


def parse_email(service, msg_id: str):
    msg = service.users().messages().get(userId="me", id=msg_id, format="full").execute()

    subject = get_header(msg, "Subject")
    sender = get_header(msg, "From")
    date = get_header(msg, "Date")

    body_text = extract_text_from_message(msg)

    print("\n" + "=" * 90)
    print(f"From:    {sender}")
    print(f"Date:    {date}")
    print(f"Subject: {subject}")
    print("-" * 90)
    handle_email_text(body_text)
    print("=" * 90 + "\n")


def main(poll_seconds: int = 5):
    service = get_gmail_service()

    print("Listening for unread INBOX emails... (Ctrl+C to stop)")
    while True:
        try:
            unread_ids = fetch_unread_inbox_ids(service, max_results=10)

            for msg_id in unread_ids:
                try:
                    parse_email(service, msg_id)
                    mark_as_read(service, msg_id)
                    print("[Marked as read]\n")
                except Exception as e:
                    print(f"[ERROR] Failed to process email {msg_id}: {e}")
                    mark_as_read(service, msg_id)

            time.sleep(poll_seconds)

        except KeyboardInterrupt:
            print("\nStopped.")
            break
        except Exception as e:
            print(f"[ERROR] Polling error: {e}")
            time.sleep(poll_seconds)


if __name__ == "__main__":
    main(poll_seconds=3)