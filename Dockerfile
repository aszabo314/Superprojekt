# ── Build stage ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# Python needed by emscripten for WASM native compilation
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*

# Install wasm-tools workload for Blazor WASM native build
RUN dotnet workload install wasm-tools

WORKDIR /src
COPY . .

# Restore tools (paket, adaptify, etc.)
RUN dotnet tool restore

# Restore packages via paket
RUN dotnet paket restore

# Build server
RUN dotnet build src/Superserver/Superserver.fsproj -c Release

# Publish server
RUN dotnet publish src/Superserver/Superserver.fsproj -c Release -o /app/server

# Build WASM frontend
RUN dotnet build src/Superprojekt/Superprojekt.fsproj -c Release

# Publish WASM frontend (static files)
RUN dotnet publish src/Superprojekt/Superprojekt.fsproj -c Release -o /app/wasm

# ── Runtime stage ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends nginx && rm -rf /var/lib/apt/lists/*

# Copy server app
COPY --from=build /app/server /app/server

# Copy mesh data
COPY --from=build /src/src/Superserver/data /app/server/data

# Copy WASM static files for nginx
COPY --from=build /app/wasm/wwwroot /var/www/html

# Nginx config: serve static WASM + proxy /api to server
RUN cat > /etc/nginx/sites-available/default <<'EOF'
server {
    listen 80;

    root /var/www/html;
    index index.html;

    # WASM content types
    types {
        application/wasm wasm;
        application/octet-stream dll;
        application/json json;
        application/javascript js;
        text/html html;
        text/css css;
        image/svg+xml svg;
        image/png png;
        image/x-icon ico;
        image/jpeg jpg jpeg;
    }

    # Proxy /api/* to the dotnet server, stripping the /api prefix
    location /api/ {
        proxy_pass http://127.0.0.1:5000/;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # Serve static files, fallback to index.html for Blazor routing
    location / {
        try_files $uri $uri/ /index.html;
    }
}
EOF

# Startup script: run both nginx and the dotnet server
RUN cat > /app/start.sh <<'SCRIPT'
#!/bin/bash
cd /app/server
dotnet Superserver.dll --urls http://0.0.0.0:5000 &
nginx -g 'daemon off;'
SCRIPT
RUN chmod +x /app/start.sh

EXPOSE 80

ENTRYPOINT ["/app/start.sh"]
