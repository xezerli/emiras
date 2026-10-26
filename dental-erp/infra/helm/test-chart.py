#!/usr/bin/env python3
"""Helm chart təhlükəsizlik/düzgünlük yoxlaması: `helm template` çıxışını təhlil edir (helm PATH-də olmalıdır).
İstifadə: python3 infra/helm/test-chart.py [kubeconform]   (kubeconform göstərilsə sxem doğrulaması da işləyir, şəbəkə tələb edir)"""
import subprocess
import sys
import yaml

CHART = __file__.rsplit("/", 1)[0] + "/dentacore"


def render(*sets):
    cmd = ["helm", "template", "rel", CHART, "--kube-version", "1.30.0"]
    for s in sets:
        cmd += ["--set", s]
    out = subprocess.run(cmd, check=True, capture_output=True, text=True).stdout
    return out, [d for d in yaml.safe_load_all(out) if d]


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


text, docs = render()
by_kind = lambda k: [d for d in docs if d["kind"] == k]
deployments = {d["metadata"]["labels"]["app.kubernetes.io/component"]: d for d in by_kind("Deployment")}

assert set(deployments) == {"identity-api", "clinic-core-api", "billing-api", "notify-api", "workers", "gateway", "web"}, deployments.keys()
print("ok: 7 deployments")

for name, d in deployments.items():
    pod = d["spec"]["template"]["spec"]
    ps = pod["securityContext"]
    if not (ps["runAsNonRoot"] and ps["seccompProfile"]["type"] == "RuntimeDefault"):
        fail(f"{name}: pod securityContext")
    if pod["automountServiceAccountToken"] is not False:
        fail(f"{name}: service account token must not be mounted")
    for c in pod["containers"]:
        sc = c["securityContext"]
        if sc["allowPrivilegeEscalation"] or not sc["readOnlyRootFilesystem"] or sc["capabilities"]["drop"] != ["ALL"]:
            fail(f"{name}: container hardening")
        if "limits" not in c["resources"] or "memory" not in c["resources"]["limits"] or "requests" not in c["resources"]:
            fail(f"{name}: resources")
        if ":latest" in c["image"]:
            fail(f"{name}: image must not use :latest")
print("ok: every container non-root, read-only rootfs, caps dropped, resources set, no :latest")

# Sirlər yalnız mövcud Secret-dən oxunur; chart özü Secret yaratmır; heç bir sirr dəyəri düz mətn deyil
if by_kind("Secret"):
    fail("chart must not create Secret objects")
for name, d in deployments.items():
    for c in d["spec"]["template"]["spec"]["containers"]:
        for e in c.get("env", []):
            if any(w in e["name"].lower() for w in ("password", "pem", "key", "connection", "uri")) and "valueFrom" not in e and not e["name"].startswith("ReverseProxy"):
                fail(f"{name}: secret-looking env {e['name']} must come from a secretKeyRef")
            ref = e.get("valueFrom", {}).get("secretKeyRef")
            if ref and ref["name"] != "dentacore-secrets":
                fail(f"{name}: unexpected secret {ref['name']}")
print("ok: no Secret objects, secret-looking env only via secretKeyRef")

# Ən az imtiyaz: yalnız Identity imzalama açarını görür; Gateway/web heç bir sirr görmür; yalnız PII ilə işləyən servislər PII açarlarını alır
def env_names(name):
    return {e["name"] for e in deployments[name]["spec"]["template"]["spec"]["containers"][0].get("env", [])}

for name in deployments:
    holds_signing = "Jwt__SigningKeyPem" in env_names(name)
    if holds_signing != (name == "identity-api"):
        fail(f"{name}: signing key exposure")
for name in ("gateway", "web"):
    if any(e.startswith(("Security__", "ConnectionStrings__", "Jwt__", "RabbitMq__")) for e in env_names(name)):
        fail(f"{name}: must not receive backend secrets")
if "Security__PiiEncryptionKey" in env_names("notify-api"):
    fail("notify-api does not need PII keys")
print("ok: least-privilege secret distribution (signing key only in identity-api)")

# Probes
for name, d in deployments.items():
    c = d["spec"]["template"]["spec"]["containers"][0]
    for p in ("startupProbe", "livenessProbe", "readinessProbe"):
        if p not in c:
            fail(f"{name}: {p} missing")
if deployments["clinic-core-api"]["spec"]["template"]["spec"]["containers"][0]["readinessProbe"]["httpGet"]["path"] != "/health/ready":
    fail("backend readiness must check dependencies")
if deployments["clinic-core-api"]["spec"]["template"]["spec"]["containers"][0]["livenessProbe"]["httpGet"]["path"] != "/health":
    fail("liveness must not depend on the database")
print("ok: liveness is dependency-free, readiness checks the database")

# Workers: Service yoxdur, HPA yoxdur (outbox SKIP LOCKED ilə sabit replika), Gateway klasterləri release-ə uyğun
services = {d["metadata"]["labels"]["app.kubernetes.io/component"] for d in by_kind("Service")}
if "workers" in services:
    fail("workers must have no Service")
hpas = {d["spec"]["scaleTargetRef"]["name"] for d in by_kind("HorizontalPodAutoscaler")}
if "rel-dentacore-workers" in hpas or "rel-dentacore-notify-api" in hpas:
    fail("workers/notify must not autoscale by default")
gw_env = {e["name"]: e.get("value") for e in deployments["gateway"]["spec"]["template"]["spec"]["containers"][0]["env"]}
if gw_env["ReverseProxy__Clusters__billing__Destinations__d1__Address"] != "http://rel-dentacore-billing-api:8080/":
    fail("gateway must target this release's services")
for d in by_kind("Deployment"):
    if d["metadata"]["labels"]["app.kubernetes.io/component"] in ("clinic-core-api", "gateway") and "replicas" in d["spec"]:
        fail("HPA-managed deployments must not pin replicas (helm upgrade would reset scaling)")
print("ok: workers have no Service/HPA, gateway targets the release, HPA-managed deployments do not pin replicas")

# Şəbəkə siyasəti: defolt-qadağa + yalnız gateway/web açıq
np = {d["metadata"]["name"] for d in by_kind("NetworkPolicy")}
if "rel-dentacore-default-deny-ingress" not in np or len(np) != 4:
    fail(f"network policies: {np}")
print("ok: default-deny ingress + 3 allow policies")

# Ingress: yalnız gateway və web; /v1 və /hubs gateway-ə
ing = by_kind("Ingress")[0]
paths = {p["path"]: p["backend"]["service"]["name"] for p in ing["spec"]["rules"][0]["http"]["paths"]}
if paths != {"/v1": "rel-dentacore-gateway", "/hubs": "rel-dentacore-gateway", "/": "rel-dentacore-web"}:
    fail(f"ingress paths {paths}")
print("ok: ingress routes /v1 and /hubs to the gateway, rest to web")

# Miqrasiya hook-u və yedəkləmə
job = by_kind("Job")[0]
if "pre-upgrade" not in job["metadata"]["annotations"]["helm.sh/hook"] or job["spec"]["backoffLimit"] > 1:
    fail("migration job must be a pre-upgrade hook with no retry storm")
cj = {d["metadata"]["name"]: d for d in by_kind("CronJob")}
if set(cj) != {"dentacore-backup", "dentacore-restore-drill"}:
    fail(f"cronjobs {set(cj)}")   # adlar Prometheus qaydaları ilə uyğun olmalıdır
if cj["dentacore-restore-drill"]["spec"]["jobTemplate"]["spec"]["backoffLimit"] != 0:
    fail("drill must not retry silently")
print("ok: migration is a pre-upgrade hook; backup + restore-drill CronJobs named as the alert rules expect")

# Parametr variantları
_, d2 = render("tenancy.fixedTenant=klinika", "global.imageTag=sha-abc123", "networkPolicy.enabled=false", "backup.enabled=false", "migrations.enabled=false", "ingress.enabled=false")
kinds = {d["kind"] for d in d2}
if {"NetworkPolicy", "CronJob", "Job", "Ingress"} & kinds:
    fail(f"disabled features still rendered: {kinds}")
cm = [d for d in d2 if d["kind"] == "ConfigMap"][0]
if cm["data"]["Tenancy__FixedTenant"] != "klinika":
    fail("fixed tenant")
if not all(":sha-abc123" in c["image"] for d in d2 if d["kind"] == "Deployment" for c in d["spec"]["template"]["spec"]["containers"]):
    fail("image tag override")
print("ok: feature flags and overrides work")

if len(sys.argv) > 1 and sys.argv[1] == "kubeconform":
    r = subprocess.run(["kubeconform", "-strict", "-summary", "-kubernetes-version", "1.30.0", "-"], input=text, text=True, capture_output=True)
    print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr)
        sys.exit(1)
print("ALL OK")
