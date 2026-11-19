# Zelo - remove o serviço, o painel e (opcionalmente) os dados.

$ErrorActionPreference = 'Continue'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

Write-Host "Removendo o Zelo..." -ForegroundColor Cyan

# Antes de remover, oferece desfazer os ajustes da aba Otimizar (com o serviço parado).
$estadoOtim = Join-Path $env:ProgramData 'Zelo\estado\otimizacao.json'
$exeServico = Join-Path $env:ProgramFiles 'Zelo\Servico\Zelo.Service.exe'
$temAjustes = $false
if (Test-Path $estadoOtim) {
    try { $temAjustes = @((Get-Content $estadoOtim -Raw | ConvertFrom-Json).Aplicados.PSObject.Properties).Count -gt 0 } catch { }
}
if (Get-Service -Name Zelo -ErrorAction SilentlyContinue) { Stop-Service -Name Zelo -Force -ErrorAction SilentlyContinue }
if ($temAjustes -and (Test-Path $exeServico)) {
    Write-Host ""
    Write-Host "O Zelo fez ajustes no Windows pela aba Otimizar." -ForegroundColor Yellow
    $r = Read-Host "Voltar esses ajustes para como eram antes de remover o Zelo? [S/n]"
    if ($r -notmatch '^[nN]') {
        & $exeServico --desfazer-otimizacao
        Write-Host ""
    } else {
        Write-Host "Ajustes mantidos. Você ainda pode voltar o PC pelo ponto de restauração 'Zelo - antes de otimizar'."
    }
}

if (Get-Service -Name Zelo -ErrorAction SilentlyContinue) {
    Stop-Service -Name Zelo -Force -ErrorAction SilentlyContinue
    sc.exe delete Zelo | Out-Null
    Write-Host "Serviço removido."
}
Get-Process -Name Zelo.App -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'ZeloPainel' -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Zelo.lnk" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Remove-Item (Join-Path $env:ProgramFiles 'Zelo') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Programa removido."

$dados = Join-Path $env:ProgramData 'Zelo'
if (Test-Path $dados) {
    $r = Read-Host "Apagar também o histórico, os relatórios e as configurações ($dados)? [s/N]"
    if ($r -match '^[sS]') {
        Remove-Item $dados -Recurse -Force
        Write-Host "Dados apagados."
    } else {
        Write-Host "Dados mantidos em $dados."
    }
}
Write-Host "Os arquivos de backup no seu destino NÃO foram tocados." -ForegroundColor Yellow
Read-Host "Pressione Enter para fechar"
