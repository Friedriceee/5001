FROM golang:1.26-bookworm AS gosu
RUN CGO_ENABLED=0 GOBIN=/out go install github.com/tianon/gosu@v1.19

# Keep the same PostgreSQL major version and glibc locale family as the existing data volume.
FROM postgres:16-bookworm
RUN apt-get update && apt-get upgrade -y --no-install-recommends && rm -rf /var/lib/apt/lists/*
COPY --from=gosu /out/gosu /usr/local/bin/gosu
