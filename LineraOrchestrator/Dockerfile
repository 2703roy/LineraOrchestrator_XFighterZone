FROM ubuntu:24.04

WORKDIR /build

# SETUP .NET SDK for dotnet build
RUN apt-get update && apt-get install -y \
    wget curl gnupg \
    && wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y dotnet-sdk-8.0 dotnet-runtime-8.0 aspnetcore-runtime-8.0

# SETUP package fuser
RUN apt-get update && apt-get install -y psmisc

# SETUP RUST TOOLCHAIN
RUN apt-get update && apt-get install -y \
    build-essential clang pkg-config libssl-dev libclang-dev protobuf-compiler unzip git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
ENV PATH="/root/.cargo/bin:${PATH}"

RUN rustup toolchain install 1.86.0 --profile minimal
RUN rustup component add clippy rustfmt rust-src --toolchain 1.86.0
RUN rustup default 1.86.0
RUN rustup target add wasm32-unknown-unknown
ENV RUSTFLAGS="-C linker=clang"

# INSTALL LINERA
RUN cargo install --locked linera-storage-service@0.15.5 \
 && cargo install --locked linera-service@0.15.5

# Copy binaries to PATH
RUN cp /root/.cargo/bin/linera* /usr/local/bin/ \
 && chmod +x /usr/local/bin/linera*

# VERIFY install
RUN which linera && which linera-storage-server && linera --version

# COPY SOURCE VÀ BUILD .NET
COPY . .

# Build .NET project
RUN dotnet restore LineraOrchestrator.csproj
RUN dotnet build LineraOrchestrator.csproj -c Debug

RUN chmod +x /build/conwaystartup.sh

ENTRYPOINT ["/build/conwaystartup.sh"]