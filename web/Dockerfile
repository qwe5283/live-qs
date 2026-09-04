FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
# Optional corporate/proxy CA support (e.g. mainland-network MITM proxies):
# any *.pem dropped in npm-ca/ is trusted by npm during the build. The dir
# normally contains only .gitkeep, making this a no-op elsewhere.
COPY npm-ca/ /tmp/npm-ca/
RUN touch /etc/npm-extra-ca.pem && cat /tmp/npm-ca/*.pem >> /etc/npm-extra-ca.pem 2>/dev/null || true
ENV NODE_EXTRA_CA_CERTS=/etc/npm-extra-ca.pem
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
