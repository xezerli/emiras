import json, sys, urllib.request, subprocess, os
repo, tag, dest = sys.argv[1], sys.argv[2], sys.argv[3]
REG = "https://mirror.gcr.io"
def tok():
    return ""
T = tok()
def get(url, accept=None):
    req = urllib.request.Request(url, headers={**({"Accept": accept} if accept else {})})
    return urllib.request.urlopen(req)
acc = "application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json"
m = json.load(get(f"{REG}/v2/{repo}/manifests/{tag}", acc))
if "manifests" in m:
    pick = next(x for x in m["manifests"] if x["platform"]["architecture"] == "amd64" and x["platform"]["os"] == "linux")
    m = json.load(get(f"{REG}/v2/{repo}/manifests/{pick['digest']}", acc))
os.makedirs(dest, exist_ok=True)
ONLY = set(int(x) for x in os.environ.get('ONLY','').split(',') if x)
for i, layer in enumerate(m["layers"]):
    if ONLY and (i+1) not in ONLY:
        continue
    print(f"layer {i+1}/{len(m['layers'])} {layer['size']} bytes", flush=True)
    T = tok()
    resp = get(f"{REG}/v2/{repo}/blobs/{layer['digest']}")
    p = subprocess.Popen(["tar", "-xz", "-C", dest, "--exclude=dev/*"], stdin=subprocess.PIPE)
    while True:
        chunk = resp.read(1 << 20)
        if not chunk: break
        p.stdin.write(chunk)
    p.stdin.close(); p.wait()
print("done", flush=True)
