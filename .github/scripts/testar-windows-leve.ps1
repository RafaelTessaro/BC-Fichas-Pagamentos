# Teste do tablet/scripts/WindowsLeve.ps1 numa máquina Windows do GitHub (descartável):
# simular não muda nada; deixar leve muda; rodar de novo não estraga o "como era antes"; o modo caixa entra, roda e
# sai (versão original e Rede); e o desfazer volta EXATAMENTE os serviços, as tarefas agendadas, as chaves e
# valores do registro e o fuso horário de antes.
$ErrorActionPreference = 'Stop'
$raiz = Split-Path (Split-Path $PSScriptRoot)
$script = Join-Path $raiz 'tablet\scripts\WindowsLeve.ps1'
$pasta = Join-Path $env:RUNNER_TEMP 'windows-leve'
$falhas = 0
function Erro([string]$t) { Write-Host "ERRO: $t" -ForegroundColor Red; $script:falhas++ }

# ---- 1. os arquivos em si
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
$novos = 'Get-ComputerInfo', 'Get-LocalUser', 'Get-LocalGroup', 'New-LocalUser', 'Get-TimeZone', 'Set-TimeZone', '-AllUsers', 'Get-PackageProvider'
$conteudo = [System.IO.File]::ReadAllText($script)
foreach ($n in $novos) { if ($conteudo -match [regex]::Escape($n)) { Erro "usa '$n', que não existe no PowerShell 5.0 do Windows 1511" } }
if ($falhas) { exit 1 }

# ---- 2. como num tablet: um "BC Fichas" (o programa grava o HKCU Run sozinho) e o fuso de Brasília
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not (Test-Path $run)) { New-Item -Path $run | Out-Null }
New-Item -ItemType Directory -Path 'C:\BCFichas' -Force | Out-Null
Copy-Item "$env:SystemRoot\System32\whoami.exe" 'C:\BCFichas\BCFichas.exe' -Force
New-ItemProperty -Path $run -Name 'BCFichas' -Value '"C:\BCFichas\BCFichas.exe"' -PropertyType String -Force | Out-Null
$fusoOriginal = (& tzutil.exe /g)
& tzutil.exe /s 'E. South America Standard Time'

# ---- foto do que o script mexe
# Só as pastas de tarefas agendadas que o script cita (outras o Windows liga e desliga sozinho).
$pastasTarefas = @{}
foreach ($m in [regex]::Matches($conteudo, '["''](?:\$t)?\\Microsoft\\[^''"]*["'']|"\$t\\[^"]*"')) {
    $c = $m.Value.Trim('''', '"') -replace '^\$t', '\Microsoft\Windows'
    $pastasTarefas[$c] = 1
    $pastasTarefas[$c.Substring(0, $c.LastIndexOf('\'))] = 1
}
$servicos = @([regex]::Matches($conteudo, "Servico(?:-Religar)?\s+'([^']+)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
# Só as chaves que o script mexe ("|r" = com as subchaves): chave inteira do Explorer mudaria sozinha durante o teste.
$chaves = @(
    'HKLM:\SOFTWARE\Policies\Microsoft|r', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies|r', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching',
    'HKLM:\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager|r', 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl',
    'HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Autologger\AutoLogger-Diagtrack-Listener',
    'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters',
    'HKCU:\Control Panel\Desktop', 'HKCU:\Control Panel\Desktop\WindowMetrics',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved|r',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications|r', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\Software\Microsoft\Windows\DWM', 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
)
# Valores que o próprio Windows muda sozinho.
$mudamSozinhos = 'InstalledPackagedAppsRevision', 'GlobalAssocChangedCounter'
function Foto {
    $f = @{}
    foreach ($nome in $servicos) {
        $c = "HKLM:\SYSTEM\CurrentControlSet\Services\$nome"
        if (-not (Test-Path $c)) { continue }
        $s = Get-Item $c
        foreach ($n in 'Start', 'DelayedAutostart') { $v = $s.GetValue($n, $null); if ($null -ne $v) { $f["svc|$nome|$n"] = [string]$v } }
    }
    $ag = New-Object -ComObject Schedule.Service; $ag.Connect()
    $fila = New-Object System.Collections.Queue; $fila.Enqueue($ag.GetFolder('\'))
    while ($fila.Count) {
        $p = $fila.Dequeue()
        if ($pastasTarefas.ContainsKey($p.Path)) { foreach ($t in $p.GetTasks(1)) { $f["task|$($t.Path)"] = [string]$t.Enabled } }
        foreach ($s in $p.GetFolders(0)) { $fila.Enqueue($s) }
    }
    foreach ($item in $chaves) {
        $c, $r = $item.Split('|')
        if (-not (Test-Path $c)) { continue }
        $lista = @(Get-Item $c)
        if ($r) { $lista += @(Get-ChildItem $c -Recurse -ErrorAction SilentlyContinue) }
        foreach ($k in $lista) {
            # O Windows cria sozinho as chaves dos aplicativos em segundo plano (o script só mexe nas que existem).
            if ($k.Name -notmatch '\\BackgroundAccessApplications\\') { $f["key|$($k.Name)"] = 'existe' }
            foreach ($n in $k.GetValueNames()) {
                if ($mudamSozinhos -contains $n) { continue }
                $v = $k.GetValue($n, $null, 'DoNotExpandEnvironmentNames')
                if ($v -is [array]) { $v = ($v | ForEach-Object { [string]$_ }) -join ',' }
                $f["reg|$($k.Name)|$n"] = "$($k.GetValueKind($n))=$v"
            }
        }
    }
    $f['fuso'] = (& tzutil.exe /g)
    $f['sem-verao'] = [string](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation').DynamicDaylightTimeDisabled
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
# Roda o script e devolve só o código de saída (o que ele escreve vai para a tela do log).
function Rodar([string]$acao, [string[]]$extra) {
    Write-Host "`n######## WindowsLeve.ps1 -Acao $acao $extra" -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Acao $acao -SemPerguntar -Pasta $pasta @extra | Out-Host
    return $LASTEXITCODE
}
function Estado { return @((Get-Content (Join-Path $pasta 'como-era-antes.json') -Raw | ConvertFrom-Json) | ForEach-Object { $_ }) }
function Shell { return (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -ErrorAction SilentlyContinue).Shell }

$f0 = Foto
Write-Host "Foto: $($f0.Count) itens ($($servicos.Count) serviços e $($pastasTarefas.Count) pastas de tarefas que o script cita)"

# ---- 3. simular não muda nada
if ((Rodar 'Leve' @('-Simular')) -ne 0) { Erro 'a simulação terminou com erro' }
[void](Comparar $f0 (Foto) 'depois de simular')
if (Test-Path (Join-Path $pasta 'como-era-antes.json')) { Erro 'a simulação gravou o arquivo de desfazer' }

# ---- 4. deixar leve (duas vezes: a segunda não pode trocar o "como era antes")
if ((Rodar 'Leve') -ne 0) { Erro 'deixar leve terminou com erro' }
$estado1 = Get-Content (Join-Path $pasta 'como-era-antes.json') -Raw
$itens = @(Estado)
if ($itens.Count -lt 20) { Erro "o arquivo de desfazer tem só $($itens.Count) item(ns)" }
if (@($itens | Where-Object { $_ -is [array] -or -not $_.chave }).Count) { Erro 'o arquivo de desfazer tem lista dentro de lista' }
$f1 = Foto
if ($f1['reg|HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search|AllowCortana'] -ne 'DWord=0') { Erro 'a Cortana não foi desligada' }
if ($f0.ContainsKey('svc|DiagTrack|Start') -and $f1['svc|DiagTrack|Start'] -ne '4') { Erro 'a telemetria (DiagTrack) não foi desligada' }
if ($f1['svc|SysMain|Start'] -eq '4') { Erro 'o SysMain ficou desligado' }
# O Windows do GitHub já tem a regra sem horário de verão (aí o script não mexe); o 1511 do tablet ainda tem.
$tz = [System.TimeZoneInfo]::FindSystemTimeZoneById('E. South America Standard Time')
$temVerao = @(0..13 | Where-Object { $tz.IsDaylightSavingTime([DateTime]::UtcNow.AddDays(30 * $_)) }).Count -gt 0
Write-Host "Este Windows ainda tem horário de verão para Brasília: $temVerao"
if ($temVerao -and $f1['sem-verao'] -ne '1') { Erro "o horário de verão não foi tirado (fuso '$($f1['fuso'])', DynamicDaylightTimeDisabled '$($f1['sem-verao'])')" }
if (-not $temVerao -and @($itens | Where-Object { $_.chave -eq 'fuso' }).Count) { Erro 'mexeu no fuso mesmo sem horário de verão' }
$spooler = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Spooler').Start
if ($spooler -eq 4) { Erro 'mexeu na impressora (Spooler)!' }
if ((Rodar 'Leve') -ne 0) { Erro 'deixar leve (de novo) terminou com erro' }
if ((Get-Content (Join-Path $pasta 'como-era-antes.json') -Raw) -ne $estado1) { Erro 'rodar de novo mudou o "como era antes"' }

# ---- 5. modo caixa: entra, o arquivo roda (e cai na área de trabalho, nunca em tela preta), sai
$vbs = Join-Path $pasta 'modo-caixa.vbs'
if ((Rodar 'Caixa') -ne 0) { Erro 'modo caixa terminou com erro' }
$sh = Shell
if ($sh -notmatch 'modo-caixa\.vbs' -or $sh -notmatch 'C:\\BCFichas\\BCFichas\.exe') { Erro "o modo caixa não ligou certo (Shell = '$sh')" }
if (-not (Test-Path $vbs)) { Erro 'não criou o modo-caixa.vbs' }
& cscript.exe //NoLogo //B $vbs 'C:\BCFichas\BCFichas.exe' /teste
if ($LASTEXITCODE -ne 42) { Erro "o modo-caixa.vbs /teste devolveu $LASTEXITCODE" }
$explorerAntes = @(Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
# De verdade: o "programa" fecha com código 0 (como no Sair do programa) e o arquivo abre a área de trabalho,
# trocando o Shell por um momento e devolvendo depois.
$p = Start-Process -FilePath 'cscript.exe' -ArgumentList '//NoLogo', "`"$vbs`"", '"C:\BCFichas\BCFichas.exe"' -PassThru -NoNewWindow
$null = $p.Handle  # sem isto o PowerShell perde o código de saída
if (-not $p.WaitForExit(90000)) { Erro 'o modo-caixa.vbs não terminou'; $p.Kill() }
elseif ($p.ExitCode -ne 0) { Erro "o modo-caixa.vbs terminou com erro ($($p.ExitCode))" }
if ((Shell) -ne $sh) { Erro "o modo-caixa.vbs não devolveu o Shell (ficou '$(Shell)')" }
# Sem o programa (versão nova em outra pasta): também tem que terminar e abrir a área de trabalho.
$p = Start-Process -FilePath 'cscript.exe' -ArgumentList '//NoLogo', "`"$vbs`"", '"C:\NaoExiste\BCFichas.exe"' -PassThru -NoNewWindow
$null = $p.Handle  # sem isto o PowerShell perde o código de saída
if (-not $p.WaitForExit(90000)) { Erro 'o modo-caixa.vbs sem o programa não terminou'; $p.Kill() }
Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $explorerAntes -notcontains $_.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
if ((Rodar 'SairDoCaixa') -ne 0) { Erro 'sair do modo caixa terminou com erro' }
if (Shell) { Erro "sair do modo caixa não tirou o Shell ('$(Shell)')" }
if ((Rodar 'Caixa') -ne 0) { Erro 'modo caixa (de novo) terminou com erro' }

# ---- 6. desfazer volta tudo (o modo caixa junto)
if ((Rodar 'Desfazer') -ne 0) { Erro 'desfazer terminou com erro' }
$dif = Comparar $f0 (Foto) 'depois de desfazer'
Write-Host "`nDiferenças depois de desfazer: $dif"
if (Test-Path (Join-Path $pasta 'como-era-antes.json')) { Erro 'o desfazer não arquivou o como-era-antes.json' }

# ---- 7. versão Rede: o modo caixa acha o BCFichasRede.exe
Remove-ItemProperty -Path $run -Name 'BCFichas' -Force
Remove-Item 'C:\BCFichas' -Recurse -Force
New-Item -ItemType Directory -Path 'C:\BCFichasRede' -Force | Out-Null
Copy-Item "$env:SystemRoot\System32\whoami.exe" 'C:\BCFichasRede\BCFichasRede.exe' -Force
New-ItemProperty -Path $run -Name 'BCFichasRede' -Value '"C:\BCFichasRede\BCFichasRede.exe"' -PropertyType String -Force | Out-Null
if ((Rodar 'Caixa') -ne 0) { Erro 'modo caixa (Rede) terminou com erro' }
if ((Shell) -notmatch 'C:\\BCFichasRede\\BCFichasRede\.exe') { Erro "o modo caixa não achou a versão Rede (Shell = '$(Shell)')" }
if ((Rodar 'SairDoCaixa') -ne 0) { Erro 'sair do modo caixa (Rede) terminou com erro' }
if (Shell) { Erro "sair do modo caixa (Rede) não tirou o Shell ('$(Shell)')" }

& tzutil.exe /s $fusoOriginal
if ($falhas) { Write-Host "`n$falhas problema(s)." -ForegroundColor Red; exit 1 }
Write-Host "`nTudo certo." -ForegroundColor Green
