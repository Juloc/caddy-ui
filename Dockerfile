ARG CADDY_VERSION=2.11.4

FROM caddy:${CADDY_VERSION}-builder AS caddy-builder
WORKDIR /src

COPY go.mod ./
COPY cmd ./cmd
COPY caddynetcp ./caddynetcp

RUN go mod tidy
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /usr/bin/caddy ./cmd/caddy

FROM python:3.13-alpine

ENV PYTHONUNBUFFERED=1
WORKDIR /app

COPY --from=caddy-builder /usr/bin/caddy /usr/bin/caddy
COPY caddy_ui_entrypoint.py /app/caddy_ui_entrypoint.py
COPY ddns/netcup_ddns.py /app/ddns/netcup_ddns.py
COPY ui/caddy_ui.py /app/ui/caddy_ui.py

RUN mkdir -p /etc/caddy/routes /var/log/caddy

ENTRYPOINT ["python", "/app/caddy_ui_entrypoint.py"]
CMD ["caddy"]
