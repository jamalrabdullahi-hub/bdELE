"""ELE Bridge client: talk to KSP through the ELE Bridge (Alt+B in flight, 127.0.0.1:8471).

Command line:   python ele.py state            python ele.py autopilot heading=90 alt=3000 speed=200
                python ele.py action gear=0     python ele.py event "event=Toggle Guard Mode"
In scripts:     from ele import call; s = call("state")
Standard library only.
"""
import json, sys, time, urllib.parse, urllib.request

BASE = "http://127.0.0.1:8471/"

def call(path, **params):
    url = BASE + path.strip("/")
    if params:
        url += "?" + urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
    with urllib.request.urlopen(url, timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))

def wait_until(pred, timeout=120, every=0.5):
    """Poll /state until pred(state) is true; returns the state (or the last one on timeout)."""
    end = time.time() + timeout
    while True:
        s = call("state")
        if pred(s) or time.time() > end:
            return s
        time.sleep(every)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(0)
    kw = dict(a.split("=", 1) if "=" in a else (a, "1") for a in sys.argv[2:])
    print(json.dumps(call(sys.argv[1], **kw), indent=1))
