FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM alpine:3.21 AS tracing
ADD --checksum=sha256:7d497fe7f50627bd1c367bb9805a50acba69c106a03cd57659cda898c50f6bb7 https://github.com/DataDog/nginx-datadog/releases/download/v1.27.0/ngx_http_datadog_module-amd64-1.27.5.so.tgz /tmp/module.tgz
RUN mkdir /module && tar -xzf /tmp/module.tgz -C /module

FROM nginx:1.27.5-alpine
COPY --from=tracing /module/ngx_http_datadog_module.so /usr/lib/nginx/modules/
RUN sed -i '1i load_module /usr/lib/nginx/modules/ngx_http_datadog_module.so;' /etc/nginx/nginx.conf
COPY nginx.conf /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
RUN SMARTFARM_VERSION=build SMARTFARM_TRACE_SAMPLE_RATE=1.0 \
    envsubst '${SMARTFARM_VERSION} ${SMARTFARM_TRACE_SAMPLE_RATE}' \
    < /etc/nginx/templates/default.conf.template > /etc/nginx/conf.d/default.conf \
    && nginx -t
EXPOSE 80
