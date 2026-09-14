{{- define "biblioteca.fullname" -}}
{{- .Release.Name }}
{{- end -}}

{{- define "biblioteca.labels" -}}
app.kubernetes.io/name: biblioteca
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}

{{- define "biblioteca.selectorLabels" -}}
app.kubernetes.io/name: biblioteca
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "biblioteca.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- .Values.serviceAccount.name | default (include "biblioteca.fullname" .) -}}
{{- else -}}
{{- .Values.serviceAccount.name | default "default" -}}
{{- end -}}
{{- end -}}

{{- define "biblioteca.env" -}}
- name: ConnectionStrings__Postgres
  valueFrom:
    secretKeyRef:
      name: {{ .Values.existingSecret }}
      key: {{ .Values.secretKeys.postgresConnectionString }}
- name: ConnectionStrings__Redis
  valueFrom:
    secretKeyRef:
      name: {{ .Values.existingSecret }}
      key: {{ .Values.secretKeys.redisConnectionString }}
- name: Auth__SigningKey
  valueFrom:
    secretKeyRef:
      name: {{ .Values.existingSecret }}
      key: {{ .Values.secretKeys.authSigningKey }}
{{- end -}}
