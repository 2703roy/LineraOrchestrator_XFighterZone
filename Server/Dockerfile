FROM ubuntu:22.04
ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /opt/server

# Cài tối thiểu: dos2unix để chuyển EOL nếu file trên Windows; file để debug
RUN apt-get update && apt-get install -y --no-install-recommends \
    dos2unix file ca-certificates curl tzdata \
  && rm -rf /var/lib/apt/lists/*

# Copy toàn bộ artefact build và entrypoint
COPY . /opt/server
COPY start.sh /opt/server/start.sh

# Convert EOL + remove BOM (perl có sẵn trên nhiều base; nếu không có, lệnh perl sẽ fail harmless)
RUN dos2unix /opt/server/start.sh || true \
 && (perl -0777 -pe 's/^\x{FEFF}//' -i /opt/server/start.sh || true) \
 && chmod +x /opt/server/start.sh \
 && find /opt/server -type f -name "*.x86_64" -exec chmod +x {} \; || true

# (Nếu runtime Unity binary thực sự cần libssl/libunwind, hãy test và thêm cụ thể sau)
# Expose ports
EXPOSE 1111/tcp
EXPOSE 1111/udp

# Use bash to invoke script (avoids kernel exec-format issues)
ENTRYPOINT ["/bin/bash","/opt/server/start.sh"]

HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 \
  CMD pgrep -f ServerLobby.x86_64 >/dev/null || exit 1
