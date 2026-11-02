# The final upstream security release documents source-built containers.
# This development image remains unverified until a Docker build is executed.
FROM golang:1.26.10-bookworm AS build
ARG MINIO_TAG=RELEASE.2025-10-15T17-29-55Z
RUN git clone --depth 1 --branch "${MINIO_TAG}" https://github.com/minio/minio.git /src
WORKDIR /src
ENV CGO_ENABLED=0
RUN go build -trimpath -ldflags "-s -w -X github.com/minio/minio/cmd.Version=2025-10-15T17:29:55Z -X github.com/minio/minio/cmd.ReleaseTag=${MINIO_TAG}" -o /out/minio .
COPY healthcheck.go /tmp/healthcheck.go
RUN go build -trimpath -o /out/healthcheck /tmp/healthcheck.go && mkdir -p /empty-data

FROM scratch
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
COPY --from=build /out/minio /minio
COPY --from=build /out/healthcheck /healthcheck
COPY --from=build --chown=10001:10001 /empty-data /data
USER 10001:10001
WORKDIR /data
EXPOSE 9000 9001
ENTRYPOINT ["/minio"]
