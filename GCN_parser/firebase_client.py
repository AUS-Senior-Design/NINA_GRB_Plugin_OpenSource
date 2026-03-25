from datetime import datetime
import os
import firebase_admin
from firebase_admin import credentials, firestore
from dotenv import load_dotenv

load_dotenv()


_DIR = os.path.dirname(os.path.abspath(__file__))

def get_firestore_client():
    if not firebase_admin._apps:
        key_path = os.getenv("FIREBASE_SERVICE_ACCOUNT_PATH")
        if not key_path:
            raise RuntimeError("FIREBASE_SERVICE_ACCOUNT_PATH environment variable not set")
        if not os.path.isabs(key_path):
            key_path = os.path.join(_DIR, key_path)
        cred = credentials.Certificate(key_path)
        firebase_admin.initialize_app(cred)
    return firestore.client()

#utility function to delete all records grb_alerts
def clear_grb_alerts(collection_name):
    db = get_firestore_client()
    for doc in db.collection(collection_name).stream():
        doc.reference.delete()
    print(f"Cleared all records from {collection_name} collection")

#utility function to get all objects from any collection filtered by a specific field and value
def get_grb_by_field(field, field_value, collection_name = "grb_alerts"):
    """
    Retrieve GRB records from Firestore matching a specific field.
    
    Args: 
    field: str name of the field to filter by (e.g., "GRB_name").
    field_value: str value to match in the specified field (e.g., "210421A").
    collection_name: str name of the Firestore collection to query (default: "grb_alerts").

    Returns: List of dicts containing the GRB records that match the given date code.
    """
    db = get_firestore_client()
    query = db.collection(collection_name).where(field, "==", field_value)
    results = []
    for doc in query.stream():
        data = doc.to_dict()
        data["id"] = doc.id  # Include document ID in the result
        results.append(data)
    return results

def push_grb_to_firestore(parsed_data: dict, collection_name = "grb_alerts"):
    """
    Push a parsed GCN record to Firestore.
    Uses auto-generated document IDs so every alert is stored,
    including re-reports of the same GRB.
    
    Args: parsed_data: dict containing the parsed GRB data to be stored in Firestore.
            collection_name: str name of the Firestore collection to store the data in (default: "grb_alerts" for raw alerts).
    """
    # Sanitize: flatten any tuple values
    clean = {k: (v[0] if isinstance(v, tuple) else v) for k, v in parsed_data.items()}

    try:
        db = get_firestore_client()
        _ts, doc_ref = db.collection(collection_name).add(clean)
        print(f"Pushed to Firestore: grb_alerts/{doc_ref.id}")
    except Exception as e:
        print(f"[Firebase] Failed to push to Firestore: {e}")
