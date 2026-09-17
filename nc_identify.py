"""
nc_identify.py

Classification step for the Nextcloud document pipeline.

Sits alongside nc_webdav.py and imports its Nextcloud client, so all the
WebDAV plumbing (listing, downloading, moving, making folders) is reused
rather than rewritten. This file adds two things nc_webdav.py doesn't have:
the keyword classifier, and Nextcloud systemtag support.

Run it with no arguments:  python nc_identify.py
Uses the same .env / environment variables as nc_webdav.py.
"""

from xml.etree import ElementTree

import requests

from nc_webdav import Nextcloud, load_client, NS


# Folder layout in Nextcloud.
INCOMING_PATH = "Processing/Incoming"
NEEDS_REVIEW_PATH = "Processing/Needs review"
DOCUMENTS_PATH = "Documents"


# ---------------------------------------------------------------------------
# Classifier
# ---------------------------------------------------------------------------

class Marker:
    """One keyword to look for, and how much it counts toward a doc type."""

    def __init__(self, pattern, weight):
        self.pattern = pattern
        self.weight = weight


class DocType:
    """One document type (invoice/receipt/contract) and its list of markers."""

    def __init__(self, name, markers):
        self.name = name
        self.markers = markers


class Score:
    """The total score one document earned for one doc type."""

    def __init__(self, doc_type_name, value):
        self.doc_type_name = doc_type_name
        self.value = value


# Markers confirmed from real OCR'd samples.
# Ambiguous words ("date", "total") are deliberately absent rather than
# handled with exclusion logic. Matching is plain substring, chosen over
# word-boundary regex for simplicity - "total" is dropped entirely because
# SUBTOTAL contains TOTAL under substring matching.

invoice_markers = [
    Marker("bill to", 2),
    Marker("invoice no.", 3),
    Marker("subtotal", 2),
    Marker("due date", 1),
    Marker("tax", 1),
]

receipt_markers = [
    Marker("receipt", 2),
    Marker("total amount", 2),
    Marker("cash", 1),
    Marker("change", 1),
    Marker("thank you", 1),
]

contract_markers = [
    # Legalese style
    Marker("whereas", 3),
    Marker("parties", 1),
    Marker("in witness whereof", 3),
    # Plain-modern style
    Marker("party a", 2),
    Marker("party b", 2),
    Marker("scope of work", 2),
    Marker("compensation", 1),
    Marker("confidentiality", 1),
]

doc_types = [
    DocType("invoice", invoice_markers),
    DocType("receipt", receipt_markers),
    DocType("contract", contract_markers),
]


def keyword_scan(text, doc_types):
    """
    Score one document's text against every doc type.

    Returns an array of Score objects, one per doc type.
    """
    text = text.lower()
    scores = []

    for doc_type in doc_types:
        total = 0
        for marker in doc_type.markers:
            if marker.pattern in text:
                total = total + marker.weight
        scores.append(Score(doc_type.name, total))

    return scores


def verdict(scores):
    """
    Turn an array of Scores into a single answer.

    Rule: if ANY pair of scores is within 2 of each other, the result is
    too close to call - return "needs-review", regardless of which score
    is highest. This is the stricter of the two rules considered (the
    looser one only compares the top two). More documents land in review
    as a result; that is intentional.
    """
    for i in range(len(scores)):
        for j in range(i + 1, len(scores)):
            if abs(scores[i].value - scores[j].value) <= 2:
                return "needs-review"

    best = scores[0]
    for score in scores:
        if score.value > best.value:
            best = score

    return best.doc_type_name


# ---------------------------------------------------------------------------
# Systemtags - not covered by nc_webdav.py, so added here
# ---------------------------------------------------------------------------

def get_or_create_tag_id(nc, tag_name):
    """
    Find the numeric ID of a Nextcloud systemtag by name.
    Creates the tag if it doesn't exist yet.
    """
    list_url = f"{nc.base}/remote.php/dav/systemtags"

    body = """<?xml version="1.0"?>
    <d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
      <d:prop><oc:display-name/></d:prop>
    </d:propfind>"""

    response = requests.request(
        "PROPFIND",
        list_url,
        auth=nc.auth,
        headers={"Depth": "1"},
        data=body,
        timeout=30,
    )
    response.raise_for_status()

    # Walk the returned XML looking for a tag whose display name matches.
    tree = ElementTree.fromstring(response.content)
    for item in tree.findall("d:response", NS):
        name_el = item.find(".//oc:display-name", NS)
        if name_el is not None and name_el.text == tag_name:
            href = item.find("d:href", NS).text
            return href.rstrip("/").split("/")[-1]

    # Not found - create it. Nextcloud returns the new tag's URL in a header.
    create_response = requests.post(
        list_url,
        auth=nc.auth,
        json={"name": tag_name, "userVisible": True, "userAssignable": True},
        timeout=30,
    )
    create_response.raise_for_status()

    tag_url = create_response.headers["Content-Location"]
    return tag_url.rstrip("/").split("/")[-1]


def apply_tag_to_file(nc, file_id, tag_id):
    """
    Attach a tag to a file. Nextcloud's tag API works on numeric file IDs,
    not paths - nc_webdav.py's list_folder() already returns those.
    """
    url = f"{nc.base}/remote.php/dav/systemtags-relations/files/{file_id}/{tag_id}"
    response = requests.put(url, auth=nc.auth, timeout=30)
    response.raise_for_status()


# ---------------------------------------------------------------------------
# Pipeline pass
# ---------------------------------------------------------------------------

def find_sidecar_files(items):
    """Pick out the .txt sidecar files from a folder listing."""
    sidecars = []
    for item in items:
        if not item["is_folder"] and item["name"].endswith(".txt"):
            sidecars.append(item)
    return sidecars


def find_matching_main_file(base_name, items):
    """
    Given "invoice123", find the actual document ("invoice123.pdf") in the
    same folder listing. Returns None if there isn't one.
    """
    for item in items:
        if item["is_folder"]:
            continue
        if item["name"] == base_name + ".txt":
            continue
        if item["name"].startswith(base_name):
            return item
    return None


def process_incoming(nc):
    """
    One pass over the Incoming folder:
      - find each sidecar .txt and the document it belongs to
      - classify the document from its sidecar text
      - move both files to their destination
      - tag the document if the verdict was confident
    """
    items = nc.list_folder(INCOMING_PATH)
    sidecars = find_sidecar_files(items)

    if not sidecars:
        print(f"{INCOMING_PATH}: nothing to classify.")
        return

    for sidecar in sidecars:
        # "invoice123.txt" -> "invoice123"
        base_name = sidecar["name"][:-4]

        main_file = find_matching_main_file(base_name, items)
        if main_file is None:
            print(f"Skipping {sidecar['name']}: no matching document file found.")
            continue

        # The three lines that actually do the classifying.
        text = nc.read_text(sidecar["path"])
        scores = keyword_scan(text, doc_types)
        result = verdict(scores)

        # Ambiguous documents go to review untagged, for a human to sort.
        if result == "needs-review":
            destination_folder = NEEDS_REVIEW_PATH
        else:
            destination_folder = f"{DOCUMENTS_PATH}/{result}"

        # Safe to call every time - make_folder returns False if it exists.
        nc.make_folder(destination_folder)

        nc.move(main_file["path"], f"{destination_folder}/{main_file['name']}")
        nc.move(sidecar["path"], f"{destination_folder}/{sidecar['name']}")

        if result != "needs-review":
            tag_id = get_or_create_tag_id(nc, result)
            apply_tag_to_file(nc, main_file["file_id"], tag_id)

        score_summary = ", ".join(f"{s.doc_type_name}={s.value}" for s in scores)
        print(f"{main_file['name']}: {result}  ({score_summary})")


if __name__ == "__main__":
    nc = load_client()
    process_incoming(nc)
