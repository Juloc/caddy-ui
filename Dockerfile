ARG CADDY_VERSION=2.11.4

FROM caddy:${CADDY_VERSION}-builder AS caddy-builder
WORKDIR /src

COPY go.mod ./
COPY cmd ./cmd
COPY caddynetcp ./caddynetcp

RUN go mod tidy
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /usr/bin/caddy ./cmd/caddy

FROM python:3.13-alpine AS companion

ENV PYTHONUNBUFFERED=1
WORKDIR /app
ARG APP_VERSION=""

COPY caddy_ui_entrypoint.py /app/caddy_ui_entrypoint.py
COPY caddy_ui /app/caddy_ui

RUN mkdir -p /etc/caddy/routes /var/log/caddy /var/lib/caddy-ui \
    && if [ -n "$APP_VERSION" ]; then printf '%s\n' "$APP_VERSION" > /app/caddy_ui/VERSION; fi

ENTRYPOINT ["python", "/app/caddy_ui_entrypoint.py"]
CMD ["web"]

FROM companion AS bundle

COPY --from=caddy-builder /usr/bin/caddy /usr/bin/caddy

CMD ["caddy"]
