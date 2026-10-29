#!/bin/bash
# deploy_results.sh - run ON THE VPS as root (copied there with rb_lobby.c, rb_ranked.c, rb_replay_rec.c, arena_results.py):
# builds the lobby (relay + replays + quick-match referee), keeps the running binary as rb_lobby.prev, installs the systemd
# drop-in with every lobby option and the results writer service, restarts, and rolls back to the previous binary and
# drop-ins when the new lobby does not stay up. Secrets are never in this script:
#   /etc/ssbu-rblobby/ticket_secret      the lobby-ticket secret (root 600, as before)
#   /etc/ssbu-arena-results/key          the results secret (or a Supabase service-role key) for arena_results.py (root 600)
# Result records (no internet address) are in /var/lib/ssbu-rblobby/results.jsonl (StateDirectory of the DynamicUser lobby).
#     bash deploy_results.sh [--no-restart]
set -euo pipefail
SRC="$(cd "$(dirname "$0")" && pwd)"
LOBBY=/opt/ssbu-rblobby
UNIT=ssbu-rblobby
DROP=/etc/systemd/system/$UNIT.service.d
[ -s /etc/ssbu-rblobby/ticket_secret ] || { echo "no /etc/ssbu-rblobby/ticket_secret"; exit 1; }
[ -s /etc/ssbu-arena-results/key ] || { echo "no /etc/ssbu-arena-results/key"; exit 1; }

gcc -O2 -Wall -pthread -o "$SRC/rb_lobby.new" "$SRC/rb_lobby.c"
mkdir -p /root/lobby_backup
stamp=$(date +%Y%m%d-%H%M%S)
cp -a "$LOBBY/rb_lobby" "/root/lobby_backup/rb_lobby.$stamp"
cp -a "$DROP" "/root/lobby_backup/dropins.$stamp"
cp -a "$LOBBY/rb_lobby" "$LOBBY/rb_lobby.prev"
install -m 755 "$SRC/rb_lobby.new" "$LOBBY/rb_lobby"
cp "$SRC/rb_lobby.c" "$SRC/rb_ranked.c" "$SRC/rb_replay_rec.c" "$LOBBY/" 2>/dev/null || true

# one drop-in with the whole command line (sorted after accounts.conf / replays.conf: its ExecStart wins)
cat > "$DROP/zz-arena.conf" <<'EOF'
# SSBU Arena lobby: accounts (tickets) and quick-match results (replays: replays.conf, Environment only) (tools/net/deploy_results.sh)
[Service]
LoadCredential=ticket:/etc/ssbu-rblobby/ticket_secret
StateDirectory=ssbu-rblobby
ExecStart=
ExecStart=/opt/ssbu-rblobby/rb_lobby --port 39010 --stats 60 --ticket-secret-file %d/ticket --results-file /var/lib/ssbu-rblobby/results.jsonl
EOF
mkdir -p /opt/ssbu-arena-results
install -m 644 "$SRC/arena_results.py" /opt/ssbu-arena-results/arena_results.py
cat > /etc/systemd/system/ssbu-arena-results.service <<'EOF'
[Unit]
Description=SSBU Arena quick-match results writer (the lobby's results file -> accounts database), tools/net/arena_results.py
After=network-online.target ssbu-rblobby.service
Wants=network-online.target

[Service]
ExecStart=/usr/bin/python3 /opt/ssbu-arena-results/arena_results.py --results /var/lib/private/ssbu-rblobby/results.jsonl --key-file %d/key --state /var/lib/ssbu-arena-results/offset
LoadCredential=key:/etc/ssbu-arena-results/key
StateDirectory=ssbu-arena-results
Restart=always
RestartSec=10
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
[ "${1:-}" = "--no-restart" ] && { echo "installed, not restarted"; exit 0; }
systemctl restart $UNIT
sleep 3
if ! systemctl is-active --quiet $UNIT; then
    echo "NEW LOBBY DID NOT START: rolling back"; journalctl -u $UNIT -n 20 --no-pager
    cp -a "/root/lobby_backup/rb_lobby.$stamp" "$LOBBY/rb_lobby"; rm -f "$DROP/zz-arena.conf"
    systemctl daemon-reload; systemctl restart $UNIT; systemctl is-active $UNIT; exit 1
fi
systemctl enable --now ssbu-arena-results >/dev/null 2>&1 || true
systemctl restart ssbu-arena-results
journalctl -u $UNIT -n 6 --no-pager
echo "deployed (backup: /root/lobby_backup/rb_lobby.$stamp, dropins.$stamp)"
