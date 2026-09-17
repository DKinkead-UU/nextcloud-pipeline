 """
Nextcloud WebDAV basics for the document pipeline.

Set these before running:
    export NC_URL="https://your-instance.example"
    export NC_USER="daniel"
    export NC_APP_PASSWORD="xxxxx-xxxxx-xxxxx-xxxxx-xxxxx"

Run it with no arguments to list /Processing/Incoming.
"""

import os
import sys
import posixpath
from xml.etree import ElementTree

import requests

NS = {"d": "DAV:", "oc": "http://owncloud.org/ns", "nc": "http://nextcloud.org/ns"}


class Nextcloud:
    def __init__(self, base_url, username, app_password):
        self.base = base_url.rstrip("/")
        self.user = username
        self.auth = (username, app_password)
        self.dav_root = f"{self.base}/remote.php/dav/files/{username}"

    def _url(self, path):
        return f"{self.dav_root}/{path.strip('/')}"

    def list_folder(self, path):
        """Return a list of dicts describing the direct children of a folder."""
        body = """<?xml version="1.0"?>
        <d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:prop>
            <d:getcontentlength/>
            <d:getcontenttype/>
            <d:getlastmodified/>
            <d:resourcetype/>
            <oc:fileid/>
          </d:prop>
        </d:propfind>"""

        response = requests.request(
            "PROPFIND",
            self._url(path),
            auth=self.auth,
            headers={"Depth": "1", "Content-Type": "application/xml"},
            data=body,
            timeout=30,
        )
        response.raise_for_status()

        tree = ElementTree.fromstring(response.content)
        entries = []

        for item in tree.findall("d:response", NS):
            href = item.find("d:href", NS).text
            name = posixpath.basename(href.rstrip("/"))
            is_folder = item.find(".//d:collection", NS) is not None

            # The first entry is the folder itself, not a child.
            if is_folder and name == posixpath.basename(path.strip("/")):
                continue

            size_el = item.find(".//d:getcontentlength", NS)
            type_el = item.find(".//d:getcontenttype", NS)
            id_el = item.find(".//oc:fileid", NS)

            entries.append({
                "name": requests.utils.unquote(name),
                "path": posixpath.join(path.strip("/"), requests.utils.unquote(name)),
                "is_folder": is_folder,
                "size": int(size_el.text) if size_el is not None and size_el.text else None,
                "mime": type_el.text if type_el is not None else None,
                "file_id": id_el.text if id_el is not None else None,
            })

        return entries

    def download(self, path):
        """Return the raw bytes of a file."""
        response = requests.get(self._url(path), auth=self.auth, timeout=60)
        response.raise_for_status()
        return response.content

    def read_text(self, path, encoding="utf-8"):
        return self.download(path).decode(encoding, errors="replace")

    def move(self, source, destination, overwrite=False):
        """Move a file server-side. Nothing is downloaded or re-uploaded."""
        response = requests.request(
            "MOVE",
            self._url(source),
            auth=self.auth,
            headers={
                "Destination": self._url(destination),
                "Overwrite": "T" if overwrite else "F",
            },
            timeout=30,
        )
        response.raise_for_status()
        return response.status_code

    def make_folder(self, path):
        """Create a folder. Returns False if it already existed."""
        response = requests.request("MKCOL", self._url(path), auth=self.auth, timeout=30)
        if response.status_code == 405:
            return False
        response.raise_for_status()
        return True


def load_env_file(filename=".env"):
    """Read KEY=value lines from a .env file sitting next to this script."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), filename)
    values = {}

    if not os.path.exists(path):
        return values

    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip().strip('"').strip("'")

    return values


def load_client():
    env = load_env_file()

    url = os.environ.get("NC_URL") or env.get("NC_URL")
    user = os.environ.get("NC_USER") or env.get("NC_USER")
    password = os.environ.get("NC_APP_PASSWORD") or env.get("NC_APP_PASSWORD")

    missing = [n for n, v in (("NC_URL", url), ("NC_USER", user), ("NC_APP_PASSWORD", password)) if not v]
    if missing:
        sys.exit("Missing environment variables: " + ", ".join(missing))

    if not url.startswith("https://"):
        sys.exit("NC_URL must start with https://")

    return Nextcloud(url, user, password)


if __name__ == "__main__":
    nc = load_client()
    folder = sys.argv[1] if len(sys.argv) > 1 else "Processing/Incoming"

    try:
        items = nc.list_folder(folder)
    except requests.HTTPError as exc:
        sys.exit(f"Request failed: {exc.response.status_code} {exc.response.reason}")

    if not items:
        print(f"{folder} is empty.")
    else:
        print(f"{folder} contains {len(items)} item(s):\n")
        for item in items:
            kind = "folder" if item["is_folder"] else (item["mime"] or "file")
            size = "" if item["size"] is None else f"  {item['size']:,} bytes"
            print(f"  {item['name']}  [{kind}]{size}")
