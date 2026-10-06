# Build context is the repo root (needs Directory.Build.props/Directory.Packages.props and the
# three project references), e.g.:
#   docker build -f src/Sol.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/nightly/sdk:11.0-preview-alpine AS build
ARG TARGETARCH

# Ubuntu's ports.ubuntu.com arm64 mirror (used by the Debian-based SDK images) has been
# severely degraded with sustained 503s; Alpine's package CDN is separate infrastructure.
# binutils provides objcopy (symbol stripping); gcc provides the crt objects/libgcc ld.bfd
# links against even though clang does the compiling.
RUN apk add --no-cache bash curl clang musl-dev zlib-dev binutils gcc

# global.json pins an exact preview SDK with rollForward:disable, so install that exact build
# rather than whatever a floating SDK image tag happens to resolve to.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --version 11.0.100-preview.5.26302.115 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh
ENV PATH="/usr/share/dotnet:${PATH}"

RUN case "$TARGETARCH" in \
      amd64) echo linux-musl-x64 > /rid ;; \
      arm64) echo linux-musl-arm64 > /rid ;; \
      *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac

WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Sol.Domain/Sol.Domain.csproj src/Sol.Domain/
COPY src/Sol.Application/Sol.Application.csproj src/Sol.Application/
COPY src/Sol.Infrastructure/Sol.Infrastructure.csproj src/Sol.Infrastructure/
COPY src/Sol.Api/Sol.Api.csproj src/Sol.Api/
# The cache mount keeps downloaded packages across attempts: a flaky TLS stream mid-restore
# (routine when the toolchain itself runs under emulation) then costs only the missing
# packages on retry instead of the whole download. --disable-parallel trims the concurrent
# request fanout that trips those streams in the first place.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/Sol.Api/Sol.Api.csproj -r "$(cat /rid)" --disable-parallel

COPY src/Sol.Domain/ src/Sol.Domain/
COPY src/Sol.Application/ src/Sol.Application/
COPY src/Sol.Infrastructure/ src/Sol.Infrastructure/
COPY src/Sol.Api/ src/Sol.Api/
# Set to true when building one architecture on a host of the other (e.g. --platform
# linux/amd64 from an Apple Silicon Mac): the amd64 ilc then runs under emulation, where its
# multi-threaded scanner trips an access violation in the work-stealing loop. --parallelism:1
# avoids the crashing code path entirely. Declared here so flipping it does not invalidate the
# restore layer above. CI builds on native runners and leaves this off.
ARG ILC_SINGLE_THREADED=false
# appsettings.Development.json carries dev-only key material (Ai:EncryptionKey,
# DeviceIdentity:Pepper). Shipping it in the published image would make every deployment that
# pulls this image share one AES key for stored provider API keys — secrets must arrive via
# environment (docker-compose) or volume instead.
RUN --mount=type=cache,target=/root/.nuget/packages \
    ilcArgs=""; \
    if [ "$ILC_SINGLE_THREADED" = "true" ]; then ilcArgs="-p:IlcSingleThreaded=true"; fi; \
    dotnet publish src/Sol.Api/Sol.Api.csproj -c Release -r "$(cat /rid)" --no-restore $ilcArgs \
    -o /app/publish \
    && rm -f /app/publish/appsettings.Development.json

# Native AOT output is self-contained; only shared native libs (openssl, zlib, …) are needed,
# not the .NET runtime itself. InvariantGlobalization (Directory.Build.props) drops the ICU
# dependency, so runtime-deps alone is enough.
FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:11.0-preview-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["./Sol.Api"]
