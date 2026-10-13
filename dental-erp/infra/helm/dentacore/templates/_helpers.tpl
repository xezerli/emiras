{{- define "dentacore.name" -}}{{ .Chart.Name }}{{- end -}}

{{- define "dentacore.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}{{ .Release.Name | trunc 40 | trimSuffix "-" }}{{- else -}}{{ printf "%s-%s" .Release.Name .Chart.Name | trunc 40 | trimSuffix "-" }}{{- end -}}
{{- end -}}

{{- define "dentacore.labels" -}}
app.kubernetes.io/name: {{ include "dentacore.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end -}}

{{- define "dentacore.selector" -}}
app.kubernetes.io/name: {{ include "dentacore.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "dentacore.image" -}}
{{- $tag := default .root.Chart.AppVersion .root.Values.global.imageTag -}}
{{- printf "%s/%s:%s" .root.Values.global.imageRegistry .image $tag -}}
{{- end -}}

{{/* Sir açarı → env. Hər giriş: [ENV_ADI, secret açarı, optional?] */}}
{{- define "dentacore.secretEnv" -}}
{{- $root := .root -}}
{{- $secret := $root.Values.secrets.existingSecret -}}
{{- $map := dict
  "pg" (list (list "ConnectionStrings__Default" "postgres-connection" false))
  "rabbit" (list (list "RabbitMq__Uri" "rabbitmq-uri" false))
  "redis" (list (list "ConnectionStrings__Redis" "redis-connection" true))
  "es" (list (list "Elasticsearch__Uri" "elasticsearch-uri" true))
  "pii" (list (list "Security__PiiEncryptionKey" "pii-encryption-key" false) (list "Security__PiiHashKey" "pii-hash-key" false))
  "jwtsign" (list (list "Jwt__SigningKeyPem" "jwt-signing-key-pem" false))
  "jwtpub" (list (list "Jwt__PublicKeyPem" "jwt-public-key-pem" false)) -}}
{{- range $ref := .refs }}
{{- range $e := index $map $ref }}
- name: {{ index $e 0 }}
  valueFrom:
    secretKeyRef:
      name: {{ $secret }}
      key: {{ index $e 1 }}
      {{- if index $e 2 }}
      optional: true
      {{- end }}
{{- end }}
{{- end }}
{{- end -}}
