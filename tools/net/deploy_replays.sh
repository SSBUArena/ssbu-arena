#!/usr/bin/env bash
# SSBU Arena replays on the lobby server (run ON the server as root, from a folder holding replay_uploader.py and ssbr.py):
#   sudo bash deploy_replays.sh
# - the lobby service ssbu-rblobby gets a drop-in (replays.conf): a state folder (/var/lib/ssbu-rblobby, systemd
#   StateDirectory) and SSBU_LOBBY_REPLAY_DIR=/var/lib/ssbu-rblobby/replays (= --replay-dir; needs an rb_lobby built with
#   rb_replay_rec.c). The ExecStart lines (the unit, accounts.conf, other drop-ins) are not touched.
# - the uploader service ssbu-replays-upload (/opt/ssbu-replays): sends each recorded match to the accounts database
#   (Edge Function replay-ingest) with the ingest secret /etc/ssbu-rblobby/replay_ingest_secret (root only, never in the repo).
# Undo: rm /etc/systemd/system/ssbu-rblobby.service.d/replays.conf; systemctl disable --now ssbu-replays-upload;
#       systemctl daemon-reload; systemctl restart ssbu-rblobby
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 1; }
[ -s /etc/ssbu-rblobby/replay_ingest_secret ] || { echo "/etc/ssbu-rblobby/replay_ingest_secret missing (64 hex characters; its SHA-256 is in private.replay_ingest)"; exit 1; }
chmod 600 /etc/ssbu-rblobby/replay_ingest_secret
grep -q "SSBU_LOBBY_REPLAY_DIR" /opt/ssbu-rblobby/rb_lobby || { echo "/opt/ssbu-rblobby/rb_lobby cannot record replays: deploy the lobby built with rb_replay_rec.c first"; exit 1; }

install -d -m 755 /opt/ssbu-replays
install -m 644 "$HERE/replay_uploader.py" "$HERE/ssbr.py" /opt/ssbu-replays/

mkdir -p /etc/systemd/system/ssbu-rblobby.service.d
cat > /etc/systemd/system/ssbu-rblobby.service.d/replays.conf <<'UNIT'
# SSBU Arena replays (tools/net/rb_replay_rec.c): every online rollback match relayed is recorded (names and account ids, no
# addresses) in /var/lib/ssbu-rblobby/replays/pending; ssbu-replays-upload sends them on. Remove this file to stop recording.
[Service]
StateDirectory=ssbu-rblobby
StateDirectoryMode=0755
Environment=SSBU_LOBBY_REPLAY_DIR=/var/lib/ssbu-rblobby/replays
TimeoutStopSec=15
UNIT

cat > /etc/systemd/system/ssbu-replays-upload.service <<'UNIT'
[Unit]
Description=SSBU Arena replays: the lobby's recordings sent to the accounts database (no inbound port)
After=network-online.target ssbu-rblobby.service
Wants=network-online.target

[Service]
ExecStart=/usr/bin/python3 -I /opt/ssbu-replays/replay_uploader.py --dir /var/lib/ssbu-rblobby/replays --secret-file /etc/ssbu-rblobby/replay_ingest_secret
Restart=always
RestartSec=10
Nice=10
ProtectHome=yes
PrivateTmp=yes
NoNewPrivileges=yes
MemoryMax=128M

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
if [ "${RESTART_LOBBY:-1}" = 1 ]; then systemctl restart ssbu-rblobby; fi
systemctl enable --now ssbu-replays-upload
sleep 2
systemctl --no-pager status ssbu-rblobby ssbu-replays-upload | grep -E "Active|rb_lobby|python" | head -8
