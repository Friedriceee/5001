# syntax=docker/dockerfile:1

# ---- build ----------------------------------------------------------------
FROM node:20-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .
# Shows the "decline" test card on the payment step. Staging only; leave false for anything customer-facing.
ARG VITE_ENABLE_TEST_PAYMENT=false
ENV VITE_ENABLE_TEST_PAYMENT=$VITE_ENABLE_TEST_PAYMENT
RUN npm run build

# ---- runtime --------------------------------------------------------------
# Unprivileged nginx: runs as a non-root user and listens on 8080.
FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
# Apply Alpine security fixes published after the base image was built (see backend/Dockerfile).
USER root
RUN apk upgrade --no-cache
USER nginx
COPY nginx.conf /etc/nginx/conf.d/default.conf
# The original monolith stack uses `api`; the microservice stack overrides this
# build argument with `gateway` while keeping one frontend image definition.
ARG API_UPSTREAM=api
USER root
RUN sed -i "s#http://api:8080#http://${API_UPSTREAM}:8080#" /etc/nginx/conf.d/default.conf
USER nginx
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
