# Code signing policy (política de assinatura de código)

> **Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).**
>
> *Situação:* o Zelo está pedindo para entrar no programa de código aberto da SignPath Foundation. Enquanto o pedido não for aprovado, as versões continuam saindo **sem** assinatura digital (conferidas só pelo SHA-256 publicado junto). Esta página diz como vai funcionar a partir da aprovação.

## O que é assinado

Só os arquivos compilados a partir **deste** repositório, pelo GitHub Actions (computadores do próprio GitHub), a partir do código que está na ramificação `main`:

| Arquivo no pacote `Zelo-win-x64.zip` | O que é |
|---|---|
| `Instalar Zelo.exe` | instalador |
| `publicado/Instalador/Zelo.Instalador.exe` | instalador/desinstalador que fica no PC |
| `publicado/Servico/Zelo.Service.exe`, `Zelo.Service.dll`, `Zelo.Core.dll` | serviço do Windows |
| `publicado/Painel/Zelo.App.exe`, `Zelo.App.dll`, `Zelo.Core.dll` | painel |

Os arquivos do .NET que vão junto no pacote já vêm assinados pela Microsoft e não são assinados de novo. O nome do produto gravado em todos os arquivos assinados é **Zelo**, e a versão é a mesma em todos (a do `Directory.Build.props`).

## Quem faz o quê

| Papel | Quem | O que faz |
|---|---|---|
| Autor (Author) | [William Stargate (@xstargatewilliam)](https://github.com/xstargatewilliam) | Escreve e altera o código do repositório |
| Revisor (Reviewer) | [William Stargate (@xstargatewilliam)](https://github.com/xstargatewilliam) | Revisa qualquer mudança vinda de outras pessoas antes de entrar |
| Aprovador (Approver) | [William Stargate (@xstargatewilliam)](https://github.com/xstargatewilliam) | Aprova à mão, na SignPath, cada pedido de assinatura de uma versão |

Todos usam verificação em duas etapas no GitHub e na SignPath.

## Como uma versão é assinada

1. A versão nova entra na `main` com o número novo em `Directory.Build.props`.
2. O GitHub Actions compila, roda os testes automáticos e monta o pacote.
3. O pacote sem assinatura é enviado à SignPath, que confere que ele veio desta compilação.
4. O aprovador confere o pedido e aprova à mão. Sem aprovação, nada é assinado nem publicado.
5. O GitHub Actions confere as assinaturas, testa a instalação e a desinstalação de verdade e publica a versão na página Releases.

A atualização automática do Zelo confere a assinatura do pacote novo: se a versão instalada é assinada, a nova precisa ser assinada pelo mesmo editor, ou a atualização é cancelada.

## Privacidade

Veja a [política de privacidade](PRIVACIDADE.md). Resumo: o Zelo não manda nenhum dado seu para lugar nenhum. Ele só se conecta à internet para baixar atualizações (do Windows, dos programas e dele mesmo).

## Alterações no sistema e remoção

O Zelo é uma ferramenta de manutenção e muda configurações do Windows. Toda mudança fora das rotinas de manutenção é mostrada antes e pede a sua confirmação (e a do Windows, quando precisa de administrador), e cada ajuste da aba Otimizar pode ser desfeito. Para remover: **Configurações → Apps → Apps instalados → Zelo → Desinstalar**.
