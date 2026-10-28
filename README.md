# Zelo — Manutenção do Windows

Serviço do Windows + painel gráfico que cuida da manutenção do PC sozinho, todo dia, toda semana e todo mês. Ele mostra um **score de saúde** de 0 a 100 e só incomoda quando algo realmente precisa de atenção.

## Instalação (uma vez)

1. Extraia o zip numa pasta qualquer (ex.: `Documentos\Zelo`).
2. Dê dois cliques em **`INSTALAR.bat`** e aceite o pedido de administrador.
3. O script faz tudo:
   - instala o .NET 10 SDK pelo winget, se faltar;
   - compila o projeto;
   - instala em `C:\Program Files\Zelo`;
   - registra o serviço e abre o painel.

Na primeira vez leva uns 5 minutos, por causa dos downloads.

Se a compilação der erro, copie as mensagens vermelhas da janela e envie para correção.

Para remover: **`DESINSTALAR.bat`**. Seus arquivos de backup nunca são apagados.

## Depois de instalar

- Abra o painel pelo Menu Iniciar (**Zelo**) ou pelo ícone de escudo perto do relógio.
- Vá em **Configurações → Backup** e escolha as pastas e o destino (de preferência um disco externo). Sem isso, a categoria Backup fica sempre em alerta, de propósito.
- Antes de deixar rodar de verdade, use **Modo simulação** e clique em **Diária**, **Semanal** e **Mensal**. A simulação mostra o que cada tarefa faria, sem apagar, instalar ou alterar nada.

## O que cada rotina faz

| Rotina | Tarefas |
|---|---|
| **Diária** | Atualização do antivírus + verificação rápida · saúde dos discos (SMART: desgaste, temperatura, erros) · espaço livre · erros graves no Visualizador de Eventos (telas azuis, desligamentos inesperados, falhas de disco) · reinicialização pendente · limpeza de temporários antigos · limpeza da pasta Downloads (opcional, só arquivos antigos) · backup incremental |
| **Semanal** | Ponto de restauração · Windows Update (sem upgrades de versão) · drivers pelo Windows Update (opcional, sem os de vídeo) · placa de vídeo: idade do driver e software do fabricante para atualizar · atualização de programas (winget) · TRIM nos SSDs / desfragmentação só em HDs · Lixeira antiga e cache de navegadores (Chrome, Edge, Firefox, Opera/Opera GX — opcional) · programas novos na inicialização |
| **Mensal** | DISM + SFC · limpeza segura do WinSxS · verificação do sistema de arquivos (sem reiniciar) · auditoria de segurança (firewall, UAC, BitLocker, SMBv1, administradores novos) · verificação completa do antivírus · **teste do backup** (compara arquivos por SHA-256) · saúde da bateria · maiores arquivos · drivers muito antigos |

### Quando as rotinas rodam

- O serviço confere a cada 5 minutos se alguma rotina venceu.
- Ele só começa quando:
  - está dentro da janela de horário;
  - o notebook está na tomada (configurável);
  - o PC está ocioso (CPU baixa).
- Se o PC ficou desligado, a rotina roda na próxima oportunidade.
- Se passar 48h atrasada, ela ignora a janela e a ociosidade.

### O que o Zelo **não** faz (de propósito)

Ele não tem limpador de registro, "liberador de RAM", desfragmentação de SSD ou desativação de serviços. Essas coisas não trazem ganho real e podem quebrar o Windows. Programas na inicialização e drivers antigos são só **relatados**, nunca alterados.

## Onde ficam as coisas

| O quê | Onde |
|---|---|
| Programa | `C:\Program Files\Zelo` |
| Configuração, histórico, logs, relatórios HTML | `C:\ProgramData\Zelo` |
| Backup | `<destino>\Zelo-Backup\` |

## Testar pelo terminal (opcional)

Num Terminal **como administrador**:

```
"C:\Program Files\Zelo\Servico\Zelo.Service.exe" --rodar diaria --simulacao
```

Troque `diaria` por `semanal` ou `mensal`, e tire `--simulacao` para executar de verdade.

## Estrutura do código

```
src/
  Zelo.Core/      tarefas, agendamento, score de saúde, histórico, relatórios
    Tarefas/      uma classe por tarefa (TarefasDiarias/Semanais/Mensais.cs)
  Zelo.Service/   serviço do Windows (agendador + canal com o painel)
  Zelo.App/       painel WPF com ícone na bandeja e notificações
scripts/          instalar.ps1 / desinstalar.ps1
```

### Como adicionar uma tarefa

1. Crie uma classe herdando de `Tarefa`.
2. Registre a classe em `Catalogo.Todas`, em `Motor.cs`.

O resto acontece sozinho:

- painel;
- histórico;
- score;
- relatório;
- opção de desativar.

### Segurança

- O serviço roda como SYSTEM; o painel roda como o usuário, sem elevação, e conversa com o serviço por um canal local (named pipe) que recusa conexões pela rede.
- Qualquer usuário do PC pode ver o painel e disparar rotinas, mas **só administradores podem alterar as configurações** (elas decidem o que o serviço apaga e copia). Contas de administrador com UAC funcionam normalmente, sem precisar abrir o painel como administrador.

### Limitações conhecidas

- Programas instalados só para um usuário podem não ser atualizados pelo winget, porque o serviço roda como SYSTEM.
- O Windows cria no máximo um ponto de restauração a cada 24h.
- A tarefa avisa se a Restauração do Sistema estiver desligada.
- BitLocker não existe no Windows Home. Nesse caso a auditoria mostra "não disponível".
