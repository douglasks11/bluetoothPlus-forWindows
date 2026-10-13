#!/usr/bin/env bash
# Compila o Bluetooth+ (WPF) no WSL e gera um único .exe para Windows x64.
#
# Uso: bash BluetoothPlus/build.sh [pasta-destino-no-windows]
#   Destino padrão: a pasta Downloads do usuário Windows atual.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
dotnet_dir="$HOME/.dotnet"
dotnet="$dotnet_dir/dotnet"

# 1. .NET 8 SDK (instala em ~/.dotnet, sem sudo, se ainda não existir).
if ! "$dotnet" --list-sdks 2>/dev/null | grep -q '^8\.'; then
  echo ">> Instalando .NET 8 SDK em $dotnet_dir ..."
  installer="$(mktemp -d)/dotnet-install.sh"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --channel 8.0 --install-dir "$dotnet_dir"
fi
export DOTNET_ROOT="$dotnet_dir"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# 2. Publica: self-contained + single-file (definidos no .csproj).
echo ">> Compilando ..."
"$dotnet" publish "$here/BluetoothPlus.csproj" -c Release -o "$here/dist" --nologo

# 3. Copia para o Windows.
# cmd.exe nem sempre está no PATH do WSL; usa o caminho absoluto.
cmd="/mnt/c/Windows/System32/cmd.exe"
win_user="$("$cmd" /c 'echo %USERNAME%' 2>/dev/null | tr -d '\r' || true)"
dest="${1:-/mnt/c/Users/$win_user/Downloads}"
if [ -z "$win_user" ] || [ ! -d "$dest" ]; then
  echo ">> Pasta Downloads do Windows não encontrada; passe o destino: bash build.sh /mnt/c/Users/<usuario>/Downloads" >&2
  exit 1
fi
cp "$here/dist/BluetoothPlus.exe" "$dest/"
echo ">> Pronto: $dest/BluetoothPlus.exe"
