# Teste do tablet/scripts/WindowsLeve.ps1 numa máquina Windows do GitHub (descartável):
# simular não muda nada; deixar leve muda; rodar de novo não estraga o "como era antes"; o modo caixa entra e sai;
# e o desfazer volta EXATAMENTE os serviços, as tarefas agendadas, os valores do registro e o fuso de antes.
$ErrorActionPreference = 'Stop'
$raiz = Split-Path (Split-Path $PSScriptRoot)
$script = Join-Path $raiz 'tablet\scripts\WindowsLeve.ps1'
$pasta = Join-Path $env:RUNNER_TEMP 'windows-leve'
$falhas = 0
function Erro([string]$t) { Write-Host "ERRO: $t" -ForegroundColor Red; $script:falhas++ }

# ---- 1. o arquivo em si
$e = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$e)
if ($e) { $e | ForEach-Object { Erro "sintaxe: $($_.Message) (linha $($_.Extent.StartLineNumber))" }; exit 1 }
$bytes = [System.IO.File]::ReadAllBytes($script)
if ($bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) { Erro 'WindowsLeve.ps1 sem BOM (o PowerShell 5 leria os acentos errado)' }
foreach ($c in Get-ChildItem (Join-Path $raiz 'tablet') -Filter *.cmd) {
    $b = [System.IO.File]::ReadAllBytes($c.FullName)
    if ($b | Where-Object { $_ -gt 127 }) { Erro "$($c.Name) tem caractere fora do ASCII" }
    $texto = [System.Text.Encoding]::ASCII.GetString($b)
    if (($texto -replace "`r`n", '') -match "`n") { Erro "$($c.Name) não está com fim de linha do Windows (CRLF)" }
}
# Nada de comando que só existe no PowerShell 5.1 ou mais novo (o 1511 tem o 5.0).
$novos = 'Get-ComputerInfo', 'Get-LocalUser', 'Get-LocalGroup', 'New-LocalUser', 'Get-TimeZone', 'Set-TimeZone', 'Remove-AppxPackage -AllUsers', 'Get-PackageProvider'
$conteudo = [System.IO.File]::ReadAllText($script)
foreach ($n in $novos) { if ($conteudo -match [regex]::Escape($n)) { Erro "usa '$n', que não existe no PowerShell 5.0 do Windows 1511" } }
if ($falhas) { exit 1 }

# ---- 2. um "BC Fichas" de mentira, como no tablet (o programa grava o HKCU Run sozinho)
New-Item -ItemType Directory -Path 'C:\BCFichas' -Force | Out-Null
Copy-Item "$env:SystemRoot\System32\whoami.exe" 'C:\BCFichas\BCFichas.exe' -Force
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'BCFichas' -Value '"C:\BCFichas\BCFichas.exe"' -PropertyType String -Force | Out-Null

# ---- foto do que o script mexe
# Só as chaves que o script mexe ("|r" = com as subchaves): chave inteira do Explorer mudaria sozinha durante o teste.
$chaves = @(
    'HKLM:\SOFTWARE\Policies\Microsoft|r', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies|r', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching',
    'HKLM:\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config', 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl',
    'HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Autologger\AutoLogger-Diagtrack-Listener',
    'HKCU:\Control Panel\Desktop', 'HKCU:\Control Panel\Desktop\WindowMetrics',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications|r', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\Software\Microsoft\Windows\DWM', 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
)
function Foto {
    $f = @{}
    foreach ($s in Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services') {
        foreach ($n in 'Start', 'DelayedAutostart') {
            $v = $s.GetValue($n, $null); if ($null -ne $v) { $f["svc|$($s.PSChildName)|$n"] = [string]$v }
        }
    }
    $ag = New-Object -ComObject Schedule.Service; $ag.Connect()
    $fila = New-Object System.Collections.Queue; $fila.Enqueue($ag.GetFolder('\'))
    while ($fila.Count) {
        $p = $fila.Dequeue()
        foreach ($t in $p.GetTasks(1)) { $f["task|$($t.Path)"] = [string]$t.Enabled }
        foreach ($s in $p.GetFolders(0)) { $fila.Enqueue($s) }
    }
    foreach ($item in $chaves) {
        $c, $r = $item.Split('|')
        if (-not (Test-Path $c)) { continue }
        $lista = @(Get-Item $c)
        if ($r) { $lista += @(Get-ChildItem $c -Recurse -ErrorAction SilentlyContinue) }
        foreach ($k in $lista) {
            foreach ($n in $k.GetValueNames()) {
                $v = $k.GetValue($n, $null, 'DoNotExpandEnvironmentNames')
                if ($v -is [array]) { $v = ($v | ForEach-Object { [string]$_ }) -join ',' }
                $f["reg|$($k.Name)|$n"] = "$($k.GetValueKind($n))=$v"
            }
        }
    }
    $f['fuso'] = (& tzutil.exe /g)
    return $f
}
function Comparar($antes, $depois, [string]$quando) {
    $dif = 0
    foreach ($k in $antes.Keys) {
        if (-not $depois.ContainsKey($k)) { Erro "$($quando): sumiu $k (era $($antes[$k]))"; $dif++ }
        elseif ($depois[$k] -ne $antes[$k]) { Erro "$($quando): $k era '$($antes[$k])' e ficou '$($depois[$k])'"; $dif++ }
    }
    foreach ($k in $depois.Keys) { if (-not $antes.ContainsKey($k)) { Erro "$($quando): apareceu $k = $($depois[$k])"; $dif++ } }
    return $dif
}
function Rodar([string]$acao, [string[]]$extra) {
    Write-Host "`n######## WindowsLeve.ps1 -Acao $acao $extra" -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Acao $acao -SemPerguntar -Pasta $pasta @extra
    return $LASTEXITCODE
}

$f0 = Foto
Write-Host "Foto: $($f0.Count) itens"

# ---- 3. simular não muda nada
if ((Rodar 'Leve' @('-Simular')) -ne 0) { Erro 'a simulação terminou com erro' }
[void](Comparar $f0 (Foto) 'depois de simular')
if (Test-Path (Join-Path $pasta 'como-era-antes.json')) { Erro 'a simulação gravou o arquivo de desfazer' }

# ---- 4. deixar leve (duas vezes: a segunda não pode trocar o "como era antes")
if ((Rodar 'Leve') -ne 0) { Erro 'deixar leve terminou com erro' }
$estado1 = Get-Content (Join-Path $pasta 'como-era-antes.json') -Raw
$f1 = Foto
if ($f1['reg|HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search|AllowCortana'] -ne 'DWord=0') { Erro 'a Cortana não foi desligada' }
if ($f0.ContainsKey('svc|DiagTrack|Start') -and $f1['svc|DiagTrack|Start'] -ne '4') { Erro 'a telemetria (DiagTrack) não foi desligada' }
if ($f1['svc|Spooler|Start'] -ne $f0['svc|Spooler|Start']) { Erro 'mexeu na impressora (Spooler)!' }
if ($f1['svc|SysMain|Start'] -eq '4') { Erro 'o SysMain ficou desligado' }
if ((Rodar 'Leve') -ne 0) { Erro 'deixar leve (de novo) terminou com erro' }
if ((Get-Content (Join-Path $pasta 'como-era-antes.json') -Raw) -ne $estado1) { Erro 'rodar de novo mudou o "como era antes"' }

# ---- 5. modo caixa entra e sai
if ((Rodar 'Caixa') -ne 0) { Erro 'modo caixa terminou com erro' }
$shell = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -ErrorAction SilentlyContinue).Shell
if ($shell -notmatch 'modo-caixa\.vbs') { Erro "o modo caixa não ligou (Shell = '$shell')" }
if (-not (Test-Path 'C:\BCFichas\modo-caixa.vbs')) { Erro 'não criou o modo-caixa.vbs' }
if ((Rodar 'SairDoCaixa') -ne 0) { Erro 'sair do modo caixa terminou com erro' }
$shell = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -ErrorAction SilentlyContinue).Shell
if ($shell) { Erro "sair do modo caixa não tirou o Shell ('$shell')" }
if ((Rodar 'Caixa') -ne 0) { Erro 'modo caixa (de novo) terminou com erro' }

# ---- 6. desfazer volta tudo (o modo caixa junto)
if ((Rodar 'Desfazer') -ne 0) { Erro 'desfazer terminou com erro' }
$dif = Comparar $f0 (Foto) 'depois de desfazer'
Write-Host "`nDiferenças depois de desfazer: $dif"
if ($falhas) { Write-Host "`n$falhas problema(s)." -ForegroundColor Red; exit 1 }
Write-Host "`nTudo certo." -ForegroundColor Green
