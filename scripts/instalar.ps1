# Zelo - compila e instala o serviço e o painel.
# Uso: dê dois cliques em INSTALAR.bat (ele chama este script).

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

# --- Precisa de administrador -------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

function Etapa($texto) { Write-Host ""; Write-Host "==> $texto" -ForegroundColor Cyan }
function Falhar($texto) {
    Write-Host ""; Write-Host "ERRO: $texto" -ForegroundColor Red
    Read-Host "Pressione Enter para fechar"
    exit 1
}

try {
    Write-Host "Zelo - Manutenção do Windows" -ForegroundColor Green
    Write-Host "Instalação em 4 etapas. Pode levar alguns minutos na primeira vez."

    $rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $pub = Join-Path $raiz 'publicado'
    $preCompilado = (Test-Path "$pub\Servico\Zelo.Service.exe") -and (Test-Path "$pub\Painel\Zelo.App.exe") -and -not (Test-Path "$raiz\src")

    if ($preCompilado) {
        # Pacote da página Releases do GitHub: já vem compilado, não precisa do .NET.
        Etapa "1/4 Versão já compilada encontrada"
        Write-Host "OK: o .NET SDK não é necessário."
        Etapa "2/4 Compilação dispensada"
    }
    else {
        # --- 1. SDK do .NET ------------------------------------------------------
        Etapa "1/4 Verificando o .NET SDK"
        function Tem-Sdk {
            try { $l = & dotnet --list-sdks 2>$null; return [bool]($l | Where-Object { $_ -match '^(1[0-9])\.' }) }
            catch { return $false }
        }
        function Atualizar-Path {
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        }

        if (-not (Tem-Sdk) -and (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host "O .NET 10 SDK não foi encontrado. Instalando pelo winget..."
            winget install --id Microsoft.DotNet.SDK.10 -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity
            Atualizar-Path
            if (-not (Tem-Sdk)) {
                # Erro comum (0x8a15000f): a lista de pacotes do winget está corrompida. Recriar e tentar de novo.
                Write-Host "O winget falhou. Recriando as fontes do winget e tentando de novo..." -ForegroundColor Yellow
                winget source reset --force --disable-interactivity | Out-Null
                winget source update --disable-interactivity | Out-Null
                winget install --id Microsoft.DotNet.SDK.10 -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity
                Atualizar-Path
            }
        }

        if (-not (Tem-Sdk)) {
            # Plano B: script oficial da Microsoft, sem depender do winget.
            Write-Host "Instalando o .NET 10 SDK pelo instalador oficial da Microsoft (dot.net)..." -ForegroundColor Yellow
            $dirDotnet = Join-Path $env:ProgramFiles 'dotnet'
            $scriptDotnet = Join-Path $env:TEMP 'dotnet-install.ps1'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptDotnet
            & $scriptDotnet -Channel 10.0 -InstallDir $dirDotnet
            $env:Path = "$dirDotnet;" + $env:Path
            $env:DOTNET_ROOT = $dirDotnet
        }

        if (-not (Tem-Sdk)) {
            Falhar "Não foi possível instalar o .NET 10 SDK. Instale manualmente em https://dotnet.microsoft.com/download/dotnet/10.0 e rode o INSTALAR.bat de novo. Ou baixe a versão já compilada na página Releases do repositório."
        }
        Write-Host "OK: $((& dotnet --version))"

        # --- 2. Compilação -------------------------------------------------------
        Etapa "2/4 Compilando"
        if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

        & dotnet publish "$raiz\src\Zelo.Service\Zelo.Service.csproj" -c Release -r $rid --self-contained true -o "$pub\Servico" --nologo
        if ($LASTEXITCODE -ne 0) { Falhar "A compilação do serviço falhou. Copie as mensagens acima e abra uma issue no GitHub do Zelo." }
        & dotnet publish "$raiz\src\Zelo.App\Zelo.App.csproj" -c Release -r $rid --self-contained true -o "$pub\Painel" --nologo
        if ($LASTEXITCODE -ne 0) { Falhar "A compilação do painel falhou. Copie as mensagens acima e abra uma issue no GitHub do Zelo." }
    }

    # --- 3. Instalação -----------------------------------------------------------
    Etapa "3/4 Instalando em $env:ProgramFiles\Zelo"
    $dest = Join-Path $env:ProgramFiles 'Zelo'
    $svc = Get-Service -Name Zelo -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name Zelo -Force
        (Get-Service -Name Zelo).WaitForStatus('Stopped', '00:00:40')
    }
    Get-Process -Name Zelo.App -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    foreach ($parte in 'Servico', 'Painel') {
        robocopy "$pub\$parte" "$dest\$parte" /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Falhar "Não foi possível copiar os arquivos para $dest\$parte." }
    }

    # Pasta de dados: só SYSTEM e administradores alteram; usuários só leem (relatórios).
    $dados = Join-Path $env:ProgramData 'Zelo'
    New-Item -ItemType Directory -Force -Path $dados | Out-Null
    icacls "$dados" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

    $exe = "$dest\Servico\Zelo.Service.exe"
    if (-not $svc) {
        New-Service -Name Zelo -BinaryPathName "`"$exe`"" -DisplayName 'Zelo - Manutencao do Windows' `
            -Description 'Executa as rotinas diaria, semanal e mensal de manutencao do Windows.' -StartupType Automatic | Out-Null
    }
    sc.exe config Zelo start= delayed-auto | Out-Null
    sc.exe failure Zelo reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    Start-Service -Name Zelo

    # Painel: abre na bandeja ao entrar no Windows + atalho no Menu Iniciar.
    $painel = "$dest\Painel\Zelo.App.exe"
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'ZeloPainel' -Value "`"$painel`" --bandeja"
    $ws = New-Object -ComObject WScript.Shell
    $atalho = $ws.CreateShortcut("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Zelo.lnk")
    $atalho.TargetPath = $painel
    $atalho.Description = 'Zelo - Manutenção do Windows'
    $atalho.Save()

    # --- 4. Pronto ---------------------------------------------------------------
    Etapa "4/4 Pronto!"
    Write-Host "O serviço está rodando. A primeira rotina diária começa em alguns minutos, quando o PC estiver ocioso."
    Write-Host "A semanal roda a partir de amanhã e a mensal daqui a 3 dias (para não pesar tudo no mesmo dia)."
    Write-Host "Dica: configure o BACKUP na aba Configurações do painel."
    # Abre o painel sem privilégio de administrador.
    Start-Process explorer.exe -ArgumentList "`"$painel`""
}
catch {
    Falhar $_.Exception.Message
}

Write-Host ""
Read-Host "Pressione Enter para fechar"
