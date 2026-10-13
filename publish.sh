#!/usr/bin/env bash
# Primeira publicação no GitHub: cria o repositório git local e envia para
# https://github.com/douglasks11/bluetoothPlus-forWindows
#
# Antes de rodar: crie o repositório VAZIO em https://github.com/new
# (nome: bluetoothPlus-forWindows, Público, sem README/.gitignore/licença).
set -euo pipefail

cd "$(dirname "$0")"
remote="https://github.com/douglasks11/bluetoothPlus-forWindows.git"

if [ ! -d .git ]; then
  git init -b main
fi

git add .
git commit -m "Bluetooth+ 0.8.0: gerenciador de Bluetooth para Windows

Lista de dispositivos com identificação de tipo, radar de proximidade,
histórico de sinal, pareamento, bateria com avisos configuráveis por tipo,
modo Encontrar, apelidos e ocultação, temas claro/escuro, ícone na bandeja,
instalador por usuário e executável para download em download/."

git remote get-url origin >/dev/null 2>&1 || git remote add origin "$remote"
git push -u origin main
echo ">> Publicado: https://github.com/douglasks11/bluetoothPlus-forWindows"
