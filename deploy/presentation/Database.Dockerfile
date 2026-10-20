FROM golang:1.26-bookworm AS gosu
RUN CGO_ENABLED=0 GOBIN=/out go install github.com/tianon/gosu@6456aaa0f3c854d199d0f037f068eb97515b7513

# The existing postgres:16 data was initialized with Debian Trixie's glibc 2.41.
# Preserve that locale family as well as the PostgreSQL major version.
FROM postgres:16-trixie
RUN apt-get update && apt-get upgrade -y --no-install-recommends && rm -rf /var/lib/apt/lists/*
COPY --from=gosu /out/gosu /usr/local/bin/gosu
