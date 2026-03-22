ARG CUDA_IMAGE=nvidia/cuda:12.3.2-devel-ubuntu22.04
ARG COLMAP_REF=3.13.0
ARG INSTALL_COLMAP=false
ARG COLMAP_CUDA_ARCHITECTURES=75;80;86;89

FROM ${CUDA_IMAGE} AS colmap-base
ARG DEBIAN_FRONTEND=noninteractive
ARG COLMAP_REF
ARG INSTALL_COLMAP
ARG COLMAP_CUDA_ARCHITECTURES

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        libgl1 \
        libglib2.0-0; \
    mkdir -p /tmp/runtime-root /usr/local/src; \
    chmod 700 /tmp/runtime-root; \
    if [ "${INSTALL_COLMAP}" = "true" ]; then \
        apt-get install -y --no-install-recommends \
            cmake \
            ninja-build \
            build-essential \
            gcc-10 \
            g++-10 \
            git \
            pkg-config \
            wget \
            unzip \
            libboost-program-options-dev \
            libboost-graph-dev \
            libboost-system-dev \
            libeigen3-dev \
            libflann-dev \
            libfreeimage-dev \
            libmetis-dev \
            libgoogle-glog-dev \
            libgtest-dev \
            libgmock-dev \
            libsqlite3-dev \
            libglew-dev \
            qt6-base-dev \
            libqt6opengl6-dev \
            libcgal-dev \
            libceres-dev \
            libsuitesparse-dev \
            libcurl4-openssl-dev \
            libssl-dev \
            libopenimageio-dev \
            openimageio-tools \
            libomp-dev; \
        git clone --branch "${COLMAP_REF}" --depth 1 https://github.com/colmap/colmap.git /usr/local/src/colmap; \
        CC=/usr/bin/gcc-10 CXX=/usr/bin/g++-10 CUDAHOSTCXX=/usr/bin/g++-10 \
        cmake -S /usr/local/src/colmap -B /usr/local/src/colmap/build -GNinja \
            -DCMAKE_BUILD_TYPE=Release \
            "-DCMAKE_CUDA_ARCHITECTURES=${COLMAP_CUDA_ARCHITECTURES}"; \
        cmake --build /usr/local/src/colmap/build --parallel; \
        cmake --install /usr/local/src/colmap/build; \
        rm -rf /usr/local/src/colmap; \
    fi; \
    rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/Recon.Api/Recon.Api.csproj -c Release -o /app/publish

FROM colmap-base AS final
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 8.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm -f /tmp/dotnet-install.sh

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_ROOT=/usr/share/dotnet
ENV XDG_RUNTIME_DIR=/tmp/runtime-root

ENTRYPOINT ["dotnet", "Recon.Api.dll"]
