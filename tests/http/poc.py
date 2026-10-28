import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("BASE_URL", "http://localhost:5080")


def call(method, path, body=None, cookie=None):
    headers = {"Accept": "application/json"}
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    if cookie:
        headers["Cookie"] = cookie
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, r.read().decode("utf-8", "replace"), r.headers.get_all("Set-Cookie") or []
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace"), e.headers.get_all("Set-Cookie") or []


def main():
    # py -3 tests/http/poc.py <alice|bob|morgan|anon> <METHOD> <path> ["key=value" ...]
    user, method, path = sys.argv[1], sys.argv[2].upper(), sys.argv[3]
    pairs = [a.split("=", 1) for a in sys.argv[4:]]
    body = {k: v for k, v in pairs} if pairs else None
    cookie = None
    if user != "anon":
        password = os.environ.get("SeedUsers__Password")
        if not password:
            sys.exit("SeedUsers__Password is not set")
        status, _, cookies = call("POST", "/api/auth/login", {"userName": user, "password": password})
        print(f"login_status={status}")
        if status >= 300:
            sys.exit(1)
        cookie = "; ".join(c.split(";")[0] for c in cookies)
    status, text, _ = call(method, path, body, cookie)
    print(f"{user} {method} {path}")
    print(f"status={status}")
    print(text)


main()