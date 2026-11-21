#!/bin/sh
set -eu

package=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
install -d "$HOME/.local/bin" "$unit_dir"
binary="$package/commandtray"
if [ ! -f "$binary" ]; then binary="$package/../commandtray"; fi
install -m 0755 "$binary" "$HOME/.local/bin/commandtray"
install -m 0644 "$package/commandtray.service" "$unit_dir/commandtray.service"
systemctl --user daemon-reload
printf '%s\n' 'Installed CommandTray. Start it with systemctl --user enable --now commandtray.'
