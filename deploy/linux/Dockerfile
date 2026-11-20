# The Multiplex server for a Linux host (issue #97). scripts/Deploy-MultiplexLinux.ps1 publishes the
# server on the deploying PC into ./app, uploads this folder, and builds the image on the server, so
# the server needs only Docker. x64 only: Google Chrome is built for x64 only on Linux.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Google Chrome for the sign-in browsers (the packaged Chrome; PuppeteerSharp's download is never
# used), and a virtual display for the TripIt browser, which runs with a window because TripIt
# refuses headless Chrome. REFRESH changes once a day, so a rebuild picks up a new Chrome at most daily.
ARG REFRESH=unset
RUN echo "packages as of ${REFRESH}" \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl xvfb xauth fonts-liberation fonts-noto-color-emoji \
 && curl -fsSL -o /tmp/google-chrome.deb https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb \
 && apt-get install -y --no-install-recommends /tmp/google-chrome.deb \
 && rm -rf /tmp/google-chrome.deb /var/lib/apt/lists/*

# Loopback only: the Tailscale device shares this container's network and proxies to it. The data
# folder is absolute (an unset one would land under the working directory) and is a volume in
# compose.yaml, readable by the app user only.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://localhost:5190 \
    ASPNETCORE_HTTP_PORTS= \
    Multiplex__DataPath=/data \
    Browser__ExecutablePath=/usr/bin/google-chrome-stable \
    DISPLAY=:99 \
    HOME=/home/app

RUN mkdir -p /data /home/app && chown app:app /data /home/app && chmod 700 /data /home/app
COPY app/ /app/
COPY entrypoint.sh /entrypoint.sh

USER app
WORKDIR /app
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
