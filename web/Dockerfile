FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.28-alpine
ENV LIBATION_API_URL=http://libation:8080 \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1
COPY docker/default.conf.template /etc/nginx/templates/default.conf.template
COPY --chmod=755 docker/30-api-auth.sh /docker-entrypoint.d/30-api-auth.sh
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
