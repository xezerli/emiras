# Lokal test xidmətləri (Docker daemon olmayan mühit)

Sandbox-da Docker daemon yoxdur və `artifacts.elastic.co` bağlıdır. Rəsmi Elasticsearch image-i Docker Hub-ın Google güzgüsündən
(`mirror.gcr.io`) layer-lər kimi yüklənib çıxarıla bilir:

```bash
# 1) image-i /opt/es-root-a çıxar (≈1.4 GB). Şəbəkə kəsilərsə yalnız böyük layer-i təkrarla: ONLY=5
python3 -I infra/dev/pull_image.py library/elasticsearch 8.11.4 /opt/es-root

# 2) qeyri-root istifadəçi ilə tək node kimi işə sal (Elasticsearch root ilə işləmir)
ES=/opt/es-root/usr/share/elasticsearch
useradd -m esuser; mkdir -p /var/es/data /var/es/logs; chown -R esuser /var/es $ES/config
cat > $ES/config/elasticsearch.yml <<'YML'
cluster.name: dentacore-test
discovery.type: single-node
network.host: 127.0.0.1
http.port: 9200
xpack.security.enabled: false
path.data: /var/es/data
path.logs: /var/es/logs
action.destructive_requires_name: true
YML
printf -- '-Xms512m\n-Xmx512m\n' > $ES/config/jvm.options.d/heap.options
su esuser -c "ES_PATH_CONF=$ES/config ES_JAVA_HOME=$ES/jdk $ES/bin/elasticsearch -d -p /var/es/es.pid"

# 3) testlər
export DENTACORE_TEST_ES=http://127.0.0.1:9200
```

Docker olan mühitdə (CI) bunun əvəzinə sadəcə `docker run -e discovery.type=single-node -e xpack.security.enabled=false -p 9200:9200 elasticsearch:8.11.4`
(və ya Testcontainers) istifadə olunur: Mərhələ 9.
