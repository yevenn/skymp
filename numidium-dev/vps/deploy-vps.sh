#!/usr/bin/env bash
# Deploys Numidium's server to the VPS from this PC (Git Bash), over SSH as root
# with the numidium_vps key. The server lives in /opt/numidium/server and runs
# as the numidium user under systemd (numidium.service).
#
#   deploy-vps.sh server     install the newest Linux build from the
#                            numidium-server-linux release, then restart
#   deploy-vps.sh gamemode   copy numidium-gamemode/gamemode.js (hot reload)
#   deploy-vps.sh settings   build the VPS settings from the local
#                            server-settings.json (Linux paths), then restart
#   deploy-vps.sh plugins    upload the load order's plugins, checking hashes
#   deploy-vps.sh data       copy the local server data folder (scripts)
#   deploy-vps.sh world      replace the VPS world and accounts with this PC's
#                            (the VPS ones are kept as a dated backup)
#   deploy-vps.sh service    install or update numidium.service
#   deploy-vps.sh status | logs | restart | stop
#
# Environment: NUMIDIUM_VPS (default root@51.158.182.46), NUMIDIUM_KEY
# (default ~/.ssh/numidium_vps), NUMIDIUM_SERVER_DIR (the local server folder,
# default build/dist/server).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
vps="${NUMIDIUM_VPS:-root@51.158.182.46}"
key="${NUMIDIUM_KEY:-$HOME/.ssh/numidium_vps}"
local_server="${NUMIDIUM_SERVER_DIR:-$repo/build/dist/server}"
remote=/opt/numidium/server
release_url=https://github.com/yevenn/skymp/releases/download/numidium-server-linux
asset=numidium-server-linux.tar.gz

# Each command opens a single SSH connection: the VPS firewall blocks this PC for
# a while after 6 connections within 30 s (Git Bash's ssh can't share one)
ssh_vps() { ssh -i "$key" -o BatchMode=yes "$vps" "$@"; }

# Plugin file names from the local load order, one per line (LF)
load_order_names() {
  python - "$local_server/server-settings.json" <<'PY'
import json, os, sys
s = json.load(open(sys.argv[1], encoding="utf-8"))
sys.stdout.buffer.write("".join(os.path.basename(p) + "\n" for p in s["loadOrder"]).encode())
PY
}

# Folder holding the local load order's plugins
load_order_dir() {
  python - "$local_server/server-settings.json" <<'PY'
import json, os, sys
s = json.load(open(sys.argv[1], encoding="utf-8"))
dirs = {os.path.dirname(p) for p in s["loadOrder"]}
if len(dirs) != 1:
    sys.exit("loadOrder spans several folders: " + ", ".join(sorted(dirs)))
sys.stdout.write(dirs.pop())
PY
}

case "${1:-}" in
  server)
    ssh_vps "bash -s" <<EOF
set -euo pipefail
cd /tmp
rm -rf numidium-server-new && mkdir numidium-server-new && cd numidium-server-new
curl -fsSL -o $asset "$release_url/$asset"
curl -fsSL -o $asset.sha256 "$release_url/$asset.sha256"
sha256sum -c $asset.sha256
tar xzf $asset
systemctl stop numidium 2>/dev/null || true
rm -rf $remote/dist_back
cp -r dist_back scam_native.node package.json $remote/
chown -R numidium:numidium $remote
cd /tmp && rm -rf numidium-server-new
systemctl start numidium 2>/dev/null || echo "numidium.service isn't installed yet: run deploy-vps.sh service"
EOF
    ;;

  gamemode)
    ssh_vps "cat > $remote/gamemode.js.new && chown numidium:numidium $remote/gamemode.js.new && mv $remote/gamemode.js.new $remote/gamemode.js && echo 'gamemode.js updated'" \
      < "$repo/numidium-gamemode/gamemode.js"
    ;;

  settings)
    # Never printed: it holds the Discord client secret. Built into a private temp
    # file first, so a failure can't send an empty file, and checked on arrival.
    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
    # The remote folder goes without its leading "/": Git Bash would turn /opt/...
    # into C:/Program Files/Git/opt/... on the way to Windows' python
    python - "$local_server/server-settings.json" "${remote#/}" > "$tmp/settings.json" <<'PY'
import json, os, sys
s = json.load(open(sys.argv[1], encoding="utf-8"))
remote = "/" + sys.argv[2]
s["dataDir"] = "data"
s["loadOrder"] = [f"{remote}/plugins/{os.path.basename(p)}" for p in s["loadOrder"]]
s["gamemodePath"] = "gamemode.js"
sys.stdout.buffer.write(json.dumps(s, indent=2, ensure_ascii=False).encode() + b"\n")
PY
    ssh_vps "set -e; umask 077; cd $remote; cat > server-settings.json.new
      python3 -c 'import json,os,sys; s=json.load(open(\"server-settings.json.new\")); m=[p for p in s[\"loadOrder\"] if not os.path.exists(p)]; sys.exit(\"plugins not on the VPS: \" + \", \".join(m)) if m else None'
      chown numidium:numidium server-settings.json.new; mv server-settings.json.new server-settings.json
      echo 'server-settings.json updated'; systemctl restart numidium 2>/dev/null || true" < "$tmp/settings.json"
    ;;

  plugins)
    names="$(load_order_names)"
    dir="$(load_order_dir)"
    # The hashes travel with the plugins and are checked on arrival
    # (Git Bash's sha256sum marks names with "*")
    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
    (cd "$dir" && printf '%s' "$names" | tr '\n' '\0' | xargs -0 sha256sum | sed 's/ \*/  /') > "$tmp/SHA256SUMS.numidium"
    printf '%s\n' "$names" > "$tmp/list"
    tar czf - -C "$tmp" SHA256SUMS.numidium -C "$dir" -T "$tmp/list" |
      ssh_vps "set -e; install -d -o numidium -g numidium -m 750 $remote/plugins; cd $remote/plugins; tar xzf -
        sha256sum -c --quiet SHA256SUMS.numidium; echo \"\$(wc -l < SHA256SUMS.numidium) plugins uploaded and checked\"
        rm SHA256SUMS.numidium; chown -R numidium:numidium ."
    ;;

  data)
    (cd "$local_server" && tar czf - --exclude=data/manifest.json data) |
      ssh_vps "tar xzf - -C $remote && chown -R numidium:numidium $remote/data && echo 'data folder updated'"
    ;;

  world)
    stamp="$(date +%Y%m%d-%H%M%S)"
    (cd "$local_server" && tar czf - world numidium-accounts.json) |
      ssh_vps "set -e; systemctl stop numidium 2>/dev/null || true; cd $remote
        for f in world numidium-accounts.json; do if [ -e \$f ]; then mv \$f \$f-backup-$stamp; fi; done
        tar xzf -; chown -R numidium:numidium world numidium-accounts.json; chmod 600 numidium-accounts.json
        echo 'world and accounts replaced (old ones: *-backup-$stamp)'
        systemctl start numidium 2>/dev/null || true"
    ;;

  service)
    ssh_vps "cat > /etc/systemd/system/numidium.service && systemctl daemon-reload && systemctl enable -q numidium && systemctl restart numidium && systemctl --no-pager status numidium | head -5" \
      < "$here/numidium.service"
    ;;

  status)  ssh_vps "systemctl --no-pager status numidium | head -12" ;;
  logs)    ssh_vps "journalctl -u numidium --no-pager -n ${2:-100} -o cat" ;;
  restart) ssh_vps "systemctl restart numidium && echo restarted" ;;
  stop)    ssh_vps "systemctl stop numidium && echo stopped" ;;

  *)
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
