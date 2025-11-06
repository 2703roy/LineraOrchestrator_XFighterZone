FROM ubuntu:24.04

WORKDIR /app

# CÀI .NET SDK để build
RUN apt-get update && apt-get install -y \
    wget curl gnupg \
    && wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y dotnet-sdk-8.0 dotnet-runtime-8.0 aspnetcore-runtime-8.0

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

# CÀI LINERA & 
RUN cargo install --locked linera-storage-service@0.15.3
RUN cargo install --locked linera-service@0.15.3

# COPY BINARIES VÀO SYSTEM PATH
RUN cp /root/.cargo/bin/linera* /usr/local/bin/ \
 && cp /root/.cargo/bin/linera* /app/ \
 && chmod +x /usr/local/bin/linera*

# VERIFY
RUN which linera && which linera-storage-server && linera --version

# COPY SOURCE VÀ BUILD .NET
COPY . .
RUN dotnet build -c Debug

# COPY WASM
COPY wasm/ ./wasm/
COPY conwaystartup.sh /app/conwaystartup.sh
RUN chmod +x /app/conwaystartup.sh

ENTRYPOINT ["/app/conwaystartup.sh"]