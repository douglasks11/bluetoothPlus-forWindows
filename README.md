# Bluetooth+ para Windows

Gerenciador de Bluetooth para Windows 10/11 com interface gráfica, radar de
proximidade, identificação do tipo de cada aparelho e avisos de bateria.

Feito por **Douglas Oliveira** :) — projeto gratuito.

## Download

1. Baixe **[BluetoothPlus-0.8.0.zip](https://github.com/douglasks11/bluetoothPlus-forWindows/raw/main/download/BluetoothPlus-0.8.0.zip)**
   (pasta [`download/`](download/)).
2. Extraia o `.zip` e abra o `BluetoothPlus.exe`. Não precisa instalar o .NET: ele já vem embutido.
3. O app não é assinado digitalmente. Por isso, o navegador pode avisar que o arquivo é
   "pouco baixado" (escolha **Manter**) e o Windows pode mostrar "O Windows protegeu o computador"
   (clique em **Mais informações → Executar assim mesmo**).

**Requisitos:** Windows 10 versão 2004 (maio de 2020) ou mais novo / Windows 11, processador x64
(Intel/AMD) e um adaptador Bluetooth.

## Recursos

- Lista ao vivo em cards, separada em "Meus dispositivos" e "Por perto"
- Identificação do tipo com ícone e cor: fone, caixa de som, controle, celular, teclado,
  mouse, relógio, TV, carro etc. Usa o *Class of Device* (Clássico) ou o *Appearance*
  (BLE) declarado pelo aparelho e, na falta deles, o nome
- Radar: cada dispositivo aparece a uma distância proporcional ao RSSI (-30 dBm = centro, -100 dBm = borda)
- Gráfico do sinal ao longo do tempo para o dispositivo selecionado
- Parear (confirmação, exibição ou digitação de PIN) e remover dispositivos
- Bateria de fones Clássicos (valor publicado pelo Windows) e de dispositivos BLE (GATT), atualizada a cada 30 s
- Bateria dos aparelhos conectados na dica e no menu do ícone da bandeja, com alerta de bateria
  fraca configurável por tipo de aparelho (botão de engrenagem; padrão 20%, ou desligado).
  Ao abrir, avisa de uma vez todos os aparelhos que já estão abaixo do limite
- Modo "Encontrar": medidor quente/frio com bipe que acelera conforme você se aproxima
- Apelidos, tipo escolhido manualmente e aparelhos ocultos (salvos em `%LOCALAPPDATA%\BluetoothPlus\settings.json`);
  aparelhos sem nome e não pareados ficam ocultos por padrão
- Tema claro e escuro, ou seguindo o Windows automaticamente (botão no cabeçalho)
- Ícone na bandeja com aviso quando um dispositivo pareado conecta/desconecta
- Opção "Iniciar com o Windows" (menu do ícone na bandeja)
- Instalação por usuário embutida no próprio .exe, sem precisar de administrador
- Ligar/desligar o rádio Bluetooth
- Busca por nome, apelido, tipo ou endereço

## Instalação

Ao abrir o `BluetoothPlus.exe` pela primeira vez, ele pergunta se deve se instalar:

- **Sim**: copia para `%LOCALAPPDATA%\Programs\BluetoothPlus`, cria o atalho no
  menu Iniciar e aparece em *Configurações → Aplicativos instalados*, de onde
  pode ser desinstalado.
- **Não**: roda sem instalar e não pergunta de novo.

Fechar a janela mantém o app na bandeja. Para encerrar: botão direito no
ícone → **Sair**. Para atualizar, saia pela bandeja antes de abrir a versão nova.

## Como funciona

O painel "Bluetooth e dispositivos" do Windows é uma interface sobre APIs
WinRT públicas. O Bluetooth+ usa as mesmas:

| Função | API |
|---|---|
| Descoberta / estado (pareado, conectado) | `DeviceInformation.CreateWatcher` sobre *AssociationEndpoints* filtrados pelos protocolos Bluetooth Clássico e LE |
| RSSI em tempo real | `BluetoothLEAdvertisementWatcher` + `System.Devices.Aep.SignalStrength` |
| Pareamento | `DeviceInformationCustomPairing` |
| Bateria (Clássico) | `DEVPKEY_Bluetooth_Battery` nos nós PnP do mesmo `ContainerId` |
| Bateria (BLE) | GATT `0x180F` / `0x2A19` |
| Rádio | `Windows.Devices.Radios.Radio` |

## Compilar

Feito em C# / WPF (.NET 8). Compila no Windows ou no Linux/WSL:

```bash
bash build.sh
```

Instala o .NET 8 SDK em `~/.dotnet` se necessário, gera `dist/BluetoothPlus.exe`
(self-contained, não exige .NET no Windows) e copia para a pasta Downloads do Windows.
No Windows, com o .NET 8 SDK instalado: `dotnet publish -c Release -o dist`.

## Limitações conhecidas

- O WSL não acessa o rádio Bluetooth: o app só funciona rodando no Windows.
- A bateria só aparece em aparelhos que a informam (a maioria dos fones modernos e
  teclados/mouses BLE informa; teclados Bluetooth Clássicos geralmente não).
- O modo "Encontrar" indica a região, não o ponto exato: o sinal Bluetooth oscila bastante.
