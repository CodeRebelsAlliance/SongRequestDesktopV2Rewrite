#!/bin/sh
# Flatpak wrapper installed into /app/bin. The publish output is self-contained,
# so nothing else needs to be compiled inside the sandbox.
exec "/app/songrequest/SongRequest V2" "$@"
