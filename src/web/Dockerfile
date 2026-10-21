# PrikBoard web app: the built SPA served by nginx, which also proxies the API routes so the browser sees one origin.
# Build from this folder: docker build -t prikboard-web src/web
# Set API_URL to where the API container is reachable (default http://api:8080).
# The build output is plain static files, so the Node stage always runs on the build machine's own platform; the
# final stage only copies files, so every target platform (linux/amd64, linux/arm64) builds without emulation.
FROM --platform=$BUILDPLATFORM node:22-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:stable-alpine
ENV API_URL=http://api:8080
# The official nginx entrypoint renders templates/*.template into conf.d with envsubst at startup.
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
