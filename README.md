# 🎮 GameSir Battery Hub — Dual Controller Monitor

Aplicativo leve e portátil para Windows que monitora **simultaneamente até dois controles GameSir (ou XInput)** em tempo real, com interface moderna nos temas **Escuro** e **Claro**, teste de botões ao vivo, identificação por vibração e ícone na bandeja do sistema.

---

## ✨ Funcionalidades

- **Monitoramento Duplo Lado a Lado (P1 & P2):** Veja o nível de bateria, autonomia estimada e tempo de sessão ativa dos dois controles ao mesmo tempo.
- **Reconhecimento Automático Universal:**
  - Escaneia automaticamente os **4 slots XInput (`Slots 1 a 4`)** do Windows (`xinput1_4.dll`, `xinput1_3.dll` e `xinput9_1_0.dll`), funcionando em qualquer PC mesmo que haja controles virtuais instalados.
  - Detecta automaticamente receptores **GameSir (`VID_3537`)** — incluindo *GameSir Nova Lite*, *Cyclone 2*, *Super Nova*, *G7 / T4* — além de conexões via **Bluetooth** e controles **Xbox / XInput** padrão.
- **Identificação Instantânea (Vibração + Botões ao Vivo):**
  - Clique em **"Vibrar para identificar"** em qualquer card para fazer o respectivo controle vibrar na sua mão.
  - Aperte qualquer botão (`A, B, X, Y, LB, RB, LT, RT, D-Pad, L3, R3`) para ver o card correspondente reagir em tempo real.
- **Temas Escuro e Claro:** Alternância suave no canto superior direito com salvamento automático de preferência.
- **Bandeja do Sistema (System Tray):** Continua rodando silenciosamente perto do relógio do Windows exibindo barras duplas de bateria (`P1` e `P2`).
- **Executável 100% Portátil (`GameSirBatteryHub.exe`):** Todas as imagens e a interface já estão embutidas dentro do próprio `.exe` — basta baixar e abrir, sem precisar instalar nada.

---

## 🚀 Como Usar (Para seus amigos / outros PCs)

1. Baixe e execute o arquivo **[`GameSirBatteryHub.exe`](./GameSirBatteryHub.exe)**.
2. Ligue seu(s) controle(s) GameSir (via receptor USB 2.4GHz, Bluetooth ou cabo).
3. O aplicativo abrirá automaticamente em uma janela dedicada e mostrará os controles conectados.

> **Dica sobre o receptor 2.4GHz:** Como o receptor 2.4GHz reporta ao Windows faixas discretas de bateria via protocolo XInput (*Cheia, Média, Baixa, Crítica*), o aplicativo combina a leitura de hardware XInput com uma curva inteligente de descarga (calibrada para a bateria de 600mAh). Sempre que tirar o controle do carregador, clique em **"Ajustar → 100%"** para sincronizar o ciclo completo.

---

## 🗂️ Estrutura do Projeto

```text
GameSirBatteryMonitor/
├── GameSirBatteryHub.exe     # Executável portátil (Single-File com assets embutidos)
├── build.bat                 # Script para recompilar o .exe usando o .NET nativo do Windows
├── src/
│   └── GameSirMonitor.cs     # Código-fonte C# (XInput + SetupAPI + Servidor Local + System Tray)
├── public/
│   ├── index.html            # Interface visual (Temas Escuro e Claro)
│   └── img/
│       ├── app.ico           # Ícone do executável
│       ├── p1.png            # Render do controle P1 (Black / Cyan)
│       └── p2.png            # Render do controle P2 (White / Magenta)
├── .gitignore
├── LICENSE
└── README.md
```

---

## 🛠️ Como Compilar do Código-Fonte (Opcional)

Não é necessário instalar Visual Studio ou SDKs externos. O próprio Windows já possui o compilador do .NET Framework nativo. Basta dar dois cliques em:

```bat
build.bat
```

Isso irá gerar novamente o `GameSirBatteryHub.exe` com toda a interface e imagens embutidas.
