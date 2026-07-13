# Codex Status for WidBar

Widget WinUI 3 para mostrar o estado local do Codex na área livre da barra de tarefas do Windows 11, hospedado pelo [WidBar](https://andelby.github.io/widbar/).

## Viabilidade

O Windows 11 não oferece uma API pública para inserir uma faixa arbitrária dentro da barra de tarefas. Os Widgets oficiais vivem no painel aberto por `Win+W`, e as extensões oficiais da taskbar são voltadas a botões, progresso, overlays e Jump Lists.

O WidBar resolve essa limitação como um host de terceiros. Ele descobre widgets MSIX pelo contrato `com.widbar.widget`, posiciona o preview na área livre e hospeda flyout e configurações. Este projeto usa o [SDK e o template oficiais do WidBar](https://github.com/andelby/widbar-widget-template).

## O que o widget mostra

- Estado e atividade atual em um único texto, sem chip colorido duplicado.
- Quantidade de arquivos alterados.
- Agentes paralelos.
- Tempo decorrido.
- Um dos 47 spinners de texto é escolhido aleatoriamente para cada solicitação ativa.

Cada item pode ser habilitado ou ocultado nas configurações do widget. Também existem modo compacto e opção para ocultar o preview quando o Codex está ocioso.

O campo “Etapa X/Y” da imagem de referência não foi inventado: o Codex não expõe um total de etapas estável. O widget mostra atividade, arquivos, agentes, tempo e indicador animado, que são dados verificáveis.

Os frames dos spinners foram adaptados do projeto MIT
[Eronred/expo-agent-spinners](https://github.com/Eronred/expo-agent-spinners).
Consulte [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) para atribuição e licença.

## Como o status é obtido

O projeto usa duas fontes locais:

1. Hooks oficiais do Codex para início, ferramentas, espera, subagentes e término.
2. Leitura somente dos eventos permitidos no JSONL da sessão como fallback e para detalhes adicionais.

O bridge persiste somente estado estruturado em:

```text
%LOCALAPPDATA%\CodexTaskbarStatus\status.json
```

Prompts, comandos, respostas de ferramentas e linhas completas do transcript não são gravados no estado e não são enviados pela rede.

## Instalação local

Pré-requisitos:

- Windows 11.
- WidBar instalado pela Microsoft Store.
- .NET SDK 8 ou mais recente.
- Windows App SDK Runtime 2.2.
- Windows 10/11 SDK com MakeAppx e SignTool (necessário para gerar o MSIX assinado).
- Modo de Desenvolvedor habilitado **ou** o certificado local do pacote confiado manualmente.

O executável do widget é publicado de forma autocontida; não é necessário
instalar o runtime .NET 8 separadamente. O SDK é usado apenas para compilar.

Para instalar o WidBar de qualquer pasta:

```powershell
winget install --id 9PKLDNM83TP9 --exact --source msstore
```

### Opção A: Modo de Desenvolvedor

Abra a página do Windows e habilite o Modo de Desenvolvedor:

```powershell
start ms-settings:developers
```

Depois, execute a partir da raiz deste repositório:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-Local.ps1
```

### Opção B: pacote assinado localmente

Gere o MSIX e o certificado a partir da raiz do repositório:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Msix.ps1
```

Abra `artifacts\Codex.TaskbarStatus.Local.cer`, instale para **Usuário Atual** em **Autoridades de Certificação Raiz Confiáveis** e então execute:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-Local.ps1
```

Essa confirmação é deliberadamente manual porque altera a confiança de certificados do Windows.

### Colocar na barra

Abra o WidBar, entre em **Layout**, localize **Codex Status** e arraste-o para uma área livre da taskbar. Clique no widget para abrir os detalhes; use a engrenagem para escolher os indicadores visíveis.

No Modo de Desenvolvedor, o Windows executa o widget diretamente de
`artifacts\layout`. Mantenha essa pasta no lugar enquanto ele estiver instalado;
para atualizar os arquivos, execute novamente `Install-Local.ps1`.

Os hooks são instalados em `%USERPROFILE%\.codex\hooks.json`. O Codex exige revisão e confiança na primeira carga ou quando a definição muda. Reinicie o Codex e revise os hooks pelo fluxo apresentado pelo produto; no CLI, use `/hooks`.

O fallback JSONL funciona mesmo antes da aprovação dos hooks, mas é uma interface interna e pode precisar de atualização depois de uma mudança de schema do Codex.

## Build e testes

Na raiz do repositório:

```powershell
dotnet build .\Codex.TaskbarStatus.ExtensionApp\Codex.TaskbarStatus.ExtensionApp.csproj -c Release -p:Platform=x64
dotnet test .\Codex.TaskbarStatus.Tests\Codex.TaskbarStatus.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Msix.ps1
```

Artefatos gerados:

```text
artifacts\layout\
artifacts\Codex.TaskbarStatus_1.0.0.0_x64.msix
artifacts\Codex.TaskbarStatus.Local.cer
```

## Estrutura

```text
Codex.TaskbarStatus.ExtensionApp/  preview, flyout e configurações WidBar
Codex.TaskbarStatus.Core/          estado, parser de hooks e fallback JSONL
Codex.TaskbarStatus.Bridge/        executável chamado pelos hooks do Codex
Codex.TaskbarStatus.Tests/         testes do redutor e do parser
Codex.TaskbarStatus (Package)/     manifestos e imagens MSIX
scripts/                           build e instalação local
```

## Limitações atuais

- O WidBar ainda é beta e precisa permanecer instalado.
- A primeira instalação local exige uma ação manual de segurança do Windows.
- O JSONL do Codex é apenas fallback; o formato não é uma API estável.
- O pacote local está preparado para x64. A estrutura do projeto mantém suporte para ARM64, mas esse artefato ainda não é produzido pelo script local.
