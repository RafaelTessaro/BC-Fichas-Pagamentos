<#
    BC Fichas - Windows leve para o tablet do caixa (Windows 10 1511, 32 bits, 1 GB de RAM)

    Uso (os arquivos .cmd da pasta de cima chamam este script já como administrador):
      WindowsLeve.ps1 -Acao Leve          deixa o Windows leve (guarda antes como estava cada coisa)
      WindowsLeve.ps1 -Acao Desfazer      volta tudo o que dá para voltar ao que era antes
      WindowsLeve.ps1 -Acao Caixa         modo caixa: o Windows abre só o BC Fichas, sem a área de trabalho
      WindowsLeve.ps1 -Acao SairDoCaixa   volta a área de trabalho normal
    Opções:
      -Simular        só mostra o que faria, sem mudar nada
      -SemPerguntar   não pergunta nada: usa as respostas recomendadas (para rodar sem ninguém olhando)
      -Pasta <pasta>  onde guardar o que é preciso para desfazer (padrão: C:\ProgramData\BCFichasWindowsLeve)

    Feito para o Windows PowerShell 5.0 do Windows 10 1511: nada de recurso mais novo.
    O arquivo é gravado em UTF-8 com BOM (sem o BOM o PowerShell 5 lê os acentos errado).

    O que NÃO é mexido, de propósito (o BC Fichas, a impressora, o Bluetooth, o Wi-Fi e o VNC precisam):
    Spooler (impressora do Windows), portas COM e USB-serial, Bluetooth (bthserv), Wi-Fi e rede (WlanSvc, Dhcp,
    Dnscache, NlaSvc), firewall (MpsSvc, BFE), UltraVNC, teclado da tela (TabletInputService, ctfmon), notificações
    (WpnService), SysMain (no 1511 ele também faz a compressão da memória, que ajuda muito com 1 GB), arquivo de
    paginação, relógio (W32Time), fontes (FontCache), MachineGuid e as placas de rede (a liberação do BC Fichas).
#>
[CmdletBinding()]
param(
    [ValidateSet('Leve', 'Desfazer', 'Caixa', 'SairDoCaixa')]
    [string]$Acao = 'Leve',
    [switch]$Simular,
    [switch]$SemPerguntar,
    [string]$Pasta = (Join-Path $env:ProgramData 'BCFichasWindowsLeve')
)

$ErrorActionPreference = 'Stop'
$script:Mudancas = 0
$script:Falhas = 0
$script:Avisos = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------- mensagens e registro

$Diario = Join-Path $Pasta 'registro.txt'
$ArquivoEstado = Join-Path $Pasta 'como-era-antes.json'

function Escrever([string]$texto, [string]$cor = 'Gray') {
    Write-Host $texto -ForegroundColor $cor
    try {
        if (-not (Test-Path $Pasta)) { New-Item -ItemType Directory -Path $Pasta -Force | Out-Null }
        Add-Content -Path $Diario -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $texto) -Encoding UTF8
    } catch { }
}
function Titulo([string]$texto) { Escrever ''; Escrever "== $texto" 'Cyan' }
function Feito([string]$texto) {
    $script:Mudancas++
    if ($Simular) { Escrever "   [simulação] $texto" 'DarkYellow' } else { Escrever "   ok  $texto" 'Green' }
}
function Pulado([string]$texto) { Escrever "   --  $texto" 'DarkGray' }
function Falhou([string]$texto) { $script:Falhas++; Escrever "   !!  $texto" 'Red' }
function Aviso([string]$texto) { [void]$script:Avisos.Add($texto); Escrever "   **  $texto" 'Yellow' }

# Pergunta sim/não. Com -SemPerguntar fica a resposta recomendada.
function Perguntar([string]$pergunta, [bool]$recomendado = $true) {
    if ($SemPerguntar) { return $recomendado }
    $opcoes = if ($recomendado) { '[S/n]' } else { '[s/N]' }
    while ($true) {
        $r = Read-Host "$pergunta $opcoes"
        if ([string]::IsNullOrWhiteSpace($r)) { return $recomendado }
        switch -Regex ($r.Trim()) {
            '^(s|sim|y)$' { return $true }
            '^(n|nao|não|no)$' { return $false }
        }
    }
}

# Roda um programa do Windows (powercfg, tzutil...) e devolve a saída; o código de saída fica em $LASTEXITCODE.
function Rodar([string]$programa, [string[]]$argumentos) {
    # No PowerShell 5, com 'Stop' a primeira linha de erro do programa viraria exceção.
    $ErrorActionPreference = 'Continue'
    $saida = & $programa @argumentos 2>&1
    return ($saida | Out-String)
}

# ---------------------------------------------------------------- como era antes (para desfazer)
# Cada coisa mudada é guardada UMA vez, com o valor de antes da primeira mudança: rodar o script de novo não
# troca o "antes" pelo que já tinha sido mudado.

$script:Estado = New-Object System.Collections.ArrayList
$script:Chaves = @{}

function Carregar-Estado {
    $script:Estado = New-Object System.Collections.ArrayList
    $script:Chaves = @{}
    if (Test-Path $ArquivoEstado) {
        $texto = Get-Content -Path $ArquivoEstado -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($texto)) {
            foreach ($item in @($texto | ConvertFrom-Json)) {
                if ($null -eq $item) { continue }
                [void]$script:Estado.Add($item)
                $script:Chaves[$item.chave] = $true
            }
        }
    }
}

function Guardar-Estado {
    if ($Simular) { return }
    if (-not (Test-Path $Pasta)) { New-Item -ItemType Directory -Path $Pasta -Force | Out-Null }
    $json = ConvertTo-Json -InputObject @($script:Estado.ToArray()) -Depth 5
    $temp = "$ArquivoEstado.novo"
    [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($true)))
    Move-Item -Path $temp -Destination $ArquivoEstado -Force
}

function Lembrar($item) {
    if ($script:Chaves.ContainsKey($item.chave)) { return }
    $script:Chaves[$item.chave] = $true
    [void]$script:Estado.Add($item)
    Guardar-Estado
}

function Esquecer([string]$chave) {
    $resto = @($script:Estado.ToArray() | Where-Object { $_.chave -ne $chave })
    $script:Estado = New-Object System.Collections.ArrayList
    foreach ($i in $resto) { [void]$script:Estado.Add($i) }
    $script:Chaves.Remove($chave)
    Guardar-Estado
}

# ---------------------------------------------------------------- registro do Windows

function Ler-Valor([string]$caminho, [string]$nome) {
    if (-not (Test-Path $caminho)) { return $null }
    $chave = Get-Item -Path $caminho
    if ($chave.GetValueNames() -notcontains $nome) { return $null }
    $tipo = $chave.GetValueKind($nome).ToString()
    $valor = $chave.GetValue($nome, $null, 'DoNotExpandEnvironmentNames')
    if ($valor -is [byte[]]) { $valor = @($valor | ForEach-Object { [int]$_ }) }
    return [pscustomobject]@{ tipo = $tipo; valor = $valor }
}

function Gravar-Valor([string]$caminho, [string]$nome, $valor, [string]$tipo) {
    if (-not (Test-Path $caminho)) { New-Item -Path $caminho -Force | Out-Null }
    if ($tipo -eq 'Binary') { $valor = [byte[]]@($valor | ForEach-Object { [byte]$_ }) }
    if ($tipo -eq 'MultiString') { $valor = [string[]]@($valor) }
    New-ItemProperty -Path $caminho -Name $nome -Value $valor -PropertyType $tipo -Force | Out-Null
}

function Lembrar-Valor([string]$caminho, [string]$nome, $antes) {
    Lembrar ([pscustomobject]@{
        chave = "reg|$caminho|$nome"; tipo = 'registro'; caminho = $caminho; nome = $nome
        existia = [bool]$antes
        tipoValor = if ($antes) { $antes.tipo } else { $null }
        valor = if ($antes) { $antes.valor } else { $null }
    })
}

# Muda um valor do registro e guarda o que tinha antes (ou que não existia).
function Valor([string]$caminho, [string]$nome, $valor, [string]$tipo = 'DWord', [string]$porque = '') {
    $antes = Ler-Valor $caminho $nome
    $descricao = if ($porque) { $porque } else { "$nome = $valor" }
    if ($antes -and $antes.tipo -eq $tipo -and ((@($antes.valor) -join ',') -eq (@($valor) -join ','))) {
        Pulado "$descricao (já estava assim)"
        return
    }
    if ($Simular) { Feito $descricao; return }
    try {
        Lembrar-Valor $caminho $nome $antes
        Gravar-Valor $caminho $nome $valor $tipo
        Feito $descricao
    } catch { Falhou "$descricao : $($_.Exception.Message)" }
}

# Apaga um valor do registro (guardando o que era, para desfazer).
function Tirar-Valor([string]$caminho, [string]$nome, [string]$porque) {
    $antes = Ler-Valor $caminho $nome
    if (-not $antes) { Pulado "$porque (não tinha)"; return }
    if ($Simular) { Feito $porque; return }
    try {
        Lembrar-Valor $caminho $nome $antes
        Remove-ItemProperty -Path $caminho -Name $nome -Force
        Feito $porque
    } catch { Falhou "$porque : $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- serviços
# Pelo registro (Start: 2 automático, 3 manual, 4 desativado), guardando Start e DelayedAutostart de antes.

function Existe-Servico([string]$nome) { return (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$nome") }

function Servico([string]$nome, [ValidateSet('Desligado', 'Manual')][string]$modo, [string]$porque) {
    $caminho = "HKLM:\SYSTEM\CurrentControlSet\Services\$nome"
    if (-not (Test-Path $caminho)) { Pulado "$porque ($nome não existe neste Windows)"; return }
    $inicio = if ($modo -eq 'Desligado') { 4 } else { 3 }
    $antes = Ler-Valor $caminho 'Start'
    $atrasado = Ler-Valor $caminho 'DelayedAutostart'
    if ($antes -and [int]$antes.valor -eq $inicio) { Pulado "$porque ($nome já estava $($modo.ToLower()))"; return }
    if ($Simular) { Feito "$porque ($nome -> $($modo.ToLower()))"; return }
    try {
        Lembrar ([pscustomobject]@{
            chave = "svc|$nome"; tipo = 'servico'; nome = $nome
            start = if ($antes) { [int]$antes.valor } else { $null }
            atrasado = if ($atrasado) { [int]$atrasado.valor } else { $null }
        })
        Set-ItemProperty -Path $caminho -Name 'Start' -Value $inicio -Type DWord
        if ($modo -eq 'Desligado') {
            $s = Get-Service -Name $nome -ErrorAction SilentlyContinue
            if ($s -and $s.Status -ne 'Stopped') { Stop-Service -Name $nome -Force -ErrorAction SilentlyContinue }
        }
        Feito "$porque ($nome -> $($modo.ToLower()))"
    } catch { Falhou "$porque ($nome): $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- tarefas agendadas (pelo COM: não depende do idioma)

$script:Agendador = $null
function Agendador {
    if (-not $script:Agendador) {
        $script:Agendador = New-Object -ComObject 'Schedule.Service'
        $script:Agendador.Connect()
    }
    return $script:Agendador
}

function Achar-Tarefa([string]$caminhoCompleto) {
    $i = $caminhoCompleto.LastIndexOf('\')
    $pastaTarefa = if ($i -le 0) { '\' } else { $caminhoCompleto.Substring(0, $i) }
    $nome = $caminhoCompleto.Substring($i + 1)
    try { return (Agendador).GetFolder($pastaTarefa).GetTask($nome) } catch { return $null }
}

# Algumas tarefas do Windows Update o Windows não deixa mudar nem como administrador: aí só avisa.
function Tarefa([string]$caminhoCompleto, [string]$porque, [switch]$Tolerante) {
    $t = Achar-Tarefa $caminhoCompleto
    if (-not $t) { Pulado "$porque (tarefa $caminhoCompleto não existe)"; return }
    if (-not $t.Enabled) { Pulado "$porque ($caminhoCompleto já estava desligada)"; return }
    if ($Simular) { Feito "$porque ($caminhoCompleto)"; return }
    try {
        $t.Enabled = $false
        Lembrar ([pscustomobject]@{ chave = "task|$caminhoCompleto"; tipo = 'tarefa'; caminho = $caminhoCompleto; ativa = $true })
        Feito "$porque ($caminhoCompleto)"
    } catch {
        if ($Tolerante) { Pulado "$porque (${caminhoCompleto}: o Windows não deixa mudar)" }
        else { Falhou "$porque ($caminhoCompleto): $($_.Exception.Message)" }
    }
}

# Todas as tarefas de uma pasta (o nome pode ter * no fim).
function Tarefas-Da-Pasta([string]$pastaTarefa, [string]$filtro, [string]$porque, [switch]$Tolerante) {
    try { $p = (Agendador).GetFolder($pastaTarefa) } catch { Pulado "$porque (pasta $pastaTarefa não existe)"; return }
    $nomes = @()
    foreach ($t in $p.GetTasks(1)) { if ($t.Name -like $filtro) { $nomes += $t.Name } }
    if ($nomes.Count -eq 0) { Pulado "$porque (nenhuma tarefa em $pastaTarefa)"; return }
    foreach ($n in $nomes) { Tarefa "$pastaTarefa\$n" $porque -Tolerante:$Tolerante }
}

# ---------------------------------------------------------------- o BC Fichas neste tablet

# A pasta do BC Fichas, pelo "abrir com o Windows" que o próprio programa grava (HKCU Run, valor BCFichas).
function Achar-BCFichas {
    $v = Ler-Valor 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'BCFichas'
    if ($v) {
        $texto = [string]$v.valor
        $m = [regex]::Match($texto, '^\s*"([^"]+\.exe)"')
        if (-not $m.Success) { $m = [regex]::Match($texto, '^\s*(\S+\.exe)') }
        if ($m.Success -and (Test-Path $m.Groups[1].Value)) { return $m.Groups[1].Value }
    }
    foreach ($c in @('C:\BCFichas\BCFichas.exe', 'C:\BCFichasRede\BCFichas.exe', 'C:\BCFichasCartao\BCFichas.exe')) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Memoria {
    try {
        $so = Get-CimInstance -ClassName Win32_OperatingSystem
        $livre = [math]::Round($so.FreePhysicalMemory / 1024)
        $total = [math]::Round($so.TotalVisibleMemorySize / 1024)
        return "$($total - $livre) MB em uso de $total MB ($livre MB livres)"
    } catch { return 'não consegui medir' }
}

function Maiores-Processos {
    try {
        Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 | ForEach-Object {
            Escrever ("     {0,-28} {1,5} MB" -f $_.ProcessName, [math]::Round($_.WorkingSet64 / 1MB))
        }
    } catch { }
}

# Quem está usando o tablet (o dono do explorer.exe). As configurações "do usuário" (HKCU) valem para quem
# roda o script: se o Windows pediu a senha de OUTRO usuário para administrador, elas iriam para o outro.
function Conferir-Usuario {
    try {
        $explorer = Get-CimInstance -ClassName Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
        if (-not $explorer) { return }
        $dono = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner
        if ($dono -and $dono.User -and ($dono.User -ne $env:USERNAME)) {
            Aviso "O tablet está entrando como '$($dono.User)', mas o script está rodando como '$env:USERNAME'. As configurações de tela e de usuário vão para '$env:USERNAME'. O certo é rodar entrando como '$($dono.User)' (e ele ser administrador)."
        }
    } catch { }
}

# ---------------------------------------------------------------- partes do "deixar leve"

$TudoDoUsuario = 'HKCU:\Software\Microsoft\Windows\CurrentVersion'
$Politicas = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'

function Parte-Telemetria {
    Titulo 'Telemetria e diagnósticos (o tablet não tem internet: só gastam memória e disco)'
    Servico 'DiagTrack' 'Desligado' 'Telemetria do Windows'
    Servico 'dmwappushservice' 'Desligado' 'Mensagens de gerenciamento remoto (MDM)'
    Valor 'HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Autologger\AutoLogger-Diagtrack-Listener' 'Start' 0 'DWord' 'Registro de telemetria que liga junto com o Windows'
    Valor "$Politicas\DataCollection" 'AllowTelemetry' 0 'DWord' 'Telemetria no mínimo'
    Servico 'DcpSvc' 'Desligado' 'Coleta e publicação de dados'
    Servico 'diagnosticshub.standardcollector.service' 'Desligado' 'Coletor de diagnóstico'
    Servico 'DPS' 'Desligado' 'Serviço de diagnóstico (solução de problemas)'
    Servico 'WdiServiceHost' 'Desligado' 'Diagnóstico (serviço)'
    Servico 'WdiSystemHost' 'Desligado' 'Diagnóstico (sistema)'
    Servico 'PcaSvc' 'Desligado' 'Assistente de compatibilidade de programas'
    Servico 'TrkWks' 'Desligado' 'Rastreamento de atalhos em rede'
    $t = '\Microsoft\Windows'
    foreach ($n in 'Microsoft Compatibility Appraiser', 'ProgramDataUpdater', 'StartupAppTask', 'AitAgent') { Tarefa "$t\Application Experience\$n" 'Avaliação de compatibilidade' }
    foreach ($n in 'Consolidator', 'KernelCeipTask', 'UsbCeip', 'BthSQM', 'Uploader') { Tarefa "$t\Customer Experience Improvement Program\$n" 'Programa de experiência do cliente' }
    Tarefa "$t\Autochk\Proxy" 'Telemetria do Autochk'
    Tarefa "$t\PI\Sqm-Tasks" 'Telemetria (SQM)'
    Tarefa "$t\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector" 'Coleta de dados do disco'
    Tarefa "$t\DiskFootprint\Diagnostics" 'Diagnóstico de espaço em disco'
    Tarefa "$t\Maintenance\WinSAT" 'Teste de desempenho do Windows (pesa no Atom)'
    Tarefa "$t\Windows Error Reporting\QueueReporting" 'Envio de relatórios de erro'
    Tarefas-Da-Pasta "$t\Feedback\Siuf" 'DmClient*' 'Pesquisas de opinião do Windows'
    Tarefa "$t\Diagnosis\Scheduled" 'Diagnóstico agendado'
    Tarefa "$t\NetTrace\GatherNetworkInfo" 'Coleta de informações da rede'
    Tarefa "$t\Power Efficiency Diagnostics\AnalyzeSystem" 'Diagnóstico de energia'
    Tarefa "$t\MemoryDiagnostic\ProcessMemoryDiagnosticEvents" 'Diagnóstico de memória'
    Tarefa "$t\MemoryDiagnostic\RunFullMemoryDiagnostic" 'Diagnóstico de memória'
    Tarefa "$t\WDI\ResolutionHost" 'Diagnóstico (WDI)'
    Tarefa "$t\ErrorDetails\EnableErrorDetailsUpdate" 'Atualização de detalhes de erro'
}

function Parte-WindowsUpdate {
    Titulo 'Windows Update (sem internet ele só fica tentando)'
    Servico 'wuauserv' 'Desligado' 'Windows Update'
    Servico 'DoSvc' 'Desligado' 'Otimização de entrega de atualizações'
    Servico 'UsoSvc' 'Desligado' 'Orquestrador de atualizações'
    Tarefas-Da-Pasta '\Microsoft\Windows\WindowsUpdate' '*' 'Tarefas do Windows Update' -Tolerante
    Tarefas-Da-Pasta '\Microsoft\Windows\UpdateOrchestrator' '*' 'Tarefas do orquestrador de atualizações' -Tolerante
    # Ferramenta de "conserto" do Windows Update (KB4023057), se o tablet já esteve na internet
    Servico 'rempl' 'Desligado' 'Reparo do Windows Update (rempl)'
    Servico 'sedsvc' 'Desligado' 'Reparo do Windows Update (sedsvc)'
    Tarefas-Da-Pasta '\Microsoft\Windows\rempl' '*' 'Reparo do Windows Update' -Tolerante
    Valor 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching' 'SearchOrderConfig' 0 'DWord' 'Não procurar driver no Windows Update ao ligar a impressora (sem internet só demora)'
    Valor "$Politicas\Device Metadata" 'PreventDeviceMetadataFromNetwork' 1 'DWord' 'Não buscar ícones e dados de aparelhos na internet'
}

function Parte-Servicos {
    Titulo 'Serviços que o caixa nunca usa'
    Servico 'WSearch' 'Desligado' 'Indexação de arquivos (Windows Search)'
    Tarefa '\Microsoft\Windows\Shell\IndexerAutomaticMaintenance' 'Manutenção da indexação'
    Servico 'MapsBroker' 'Desligado' 'Mapas baixados'
    Tarefa '\Microsoft\Windows\Maps\MapsToastTask' 'Mapas'
    Tarefa '\Microsoft\Windows\Maps\MapsUpdateTask' 'Mapas'
    Servico 'lfsvc' 'Desligado' 'Localização (GPS)'
    Tarefa '\Microsoft\Windows\Location\Notifications' 'Localização'
    Tarefa '\Microsoft\Windows\Location\WindowsActionDialog' 'Localização'
    Servico 'XblAuthManager' 'Desligado' 'Xbox Live'
    Servico 'XblGameSave' 'Desligado' 'Xbox Live (jogos salvos)'
    Servico 'XboxNetApiSvc' 'Desligado' 'Xbox Live (rede)'
    Tarefa '\Microsoft\XblGameSave\XblGameSaveTask' 'Xbox Live'
    Tarefa '\Microsoft\XblGameSave\XblGameSaveTaskLogon' 'Xbox Live'
    Servico 'HomeGroupListener' 'Desligado' 'Grupo doméstico'
    Servico 'HomeGroupProvider' 'Desligado' 'Grupo doméstico'
    Servico 'OneSyncSvc' 'Desligado' 'Sincronização de e-mail, agenda e contatos'
    Servico 'RetailDemo' 'Desligado' 'Modo demonstração de loja'
    Servico 'WMPNetworkSvc' 'Desligado' 'Compartilhamento do Windows Media Player'
    Tarefa '\Microsoft\Windows\Windows Media Sharing\UpdateLibrary' 'Compartilhamento de mídia'
    Servico 'Fax' 'Desligado' 'Fax'
    Servico 'WbioSrvc' 'Desligado' 'Biometria (leitor de digital)'
    Servico 'AJRouter' 'Desligado' 'AllJoyn (casa inteligente)'
    Servico 'Browser' 'Desligado' 'Lista de computadores da rede (NetBIOS)'
    Servico 'irmon' 'Desligado' 'Infravermelho'
    Servico 'tzautoupdate' 'Desligado' 'Fuso horário automático (sem internet erra o horário de verão)'
    Tarefas-Da-Pasta '\Microsoft\Windows\Shell' 'FamilySafety*' 'Proteção para a família'
    Tarefa '\Microsoft\Windows\Speech\SpeechModelDownloadTask' 'Download de voz'
    Tarefa '\Microsoft\Windows\Mobile Broadband Accounts\MNO Metadata Parser' 'Chip de celular'
    Tarefa '\Microsoft\Windows\CloudExperienceHost\CreateObjectTask' 'Conta Microsoft'
    Tarefas-Da-Pasta '\Microsoft\Windows\SettingSync' '*' 'Sincronizar configurações na nuvem'
    Tarefa '\Microsoft\Windows\AppID\SmartScreenSpecific' 'SmartScreen'
    # IPv6 por túnel: só desliga se ninguém usa redirecionamento de porta (o BC Fichas não usa).
    $proxy = ''
    try { $proxy = (Rodar 'netsh.exe' @('interface', 'portproxy', 'show', 'all')).Trim() } catch { }
    if ([string]::IsNullOrWhiteSpace($proxy)) { Servico 'iphlpsvc' 'Desligado' 'Túneis IPv6 (IP Helper)' }
    else { Pulado 'Túneis IPv6 (IP Helper): tem redirecionamento de porta configurado, não mexi' }
    # Descoberta de aparelhos na rede (UPnP): só se nenhuma impressora usa porta de rede WSD.
    $wsd = $false
    try { $wsd = [bool](Get-PrinterPort -ErrorAction Stop | Where-Object { $_.Name -like 'WSD*' }) } catch { }
    if (-not $wsd) {
        Servico 'upnphost' 'Desligado' 'Descoberta de aparelhos na rede (UPnP)'
        Servico 'SSDPSRV' 'Desligado' 'Descoberta de aparelhos na rede (SSDP)'
        Servico 'NcdAutoSetup' 'Desligado' 'Configuração automática de aparelhos da rede'
    } else { Pulado 'Descoberta de aparelhos (UPnP): tem impressora em porta WSD, não mexi' }
}

function Parte-Defender([bool]$desligar) {
    Titulo 'Windows Defender (antivírus)'
    if (-not $desligar) { Pulado 'Você escolheu manter o Defender'; return }
    if (Test-Path "$env:ProgramData\Microsoft\Windows Defender\Platform\4.18*") {
        Aviso 'Este Defender é de uma versão nova (4.18) que ignora a desativação por política: ele pode continuar ligado.'
    }
    $d = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
    Valor $d 'DisableAntiSpyware' 1 'DWord' 'Desligar o Defender (Antimalware Service Executable)'
    Valor "$d\Real-Time Protection" 'DisableRealtimeMonitoring' 1 'DWord' 'Proteção em tempo real'
    Valor "$d\Real-Time Protection" 'DisableBehaviorMonitoring' 1 'DWord' 'Monitorar comportamento'
    Valor "$d\Real-Time Protection" 'DisableOnAccessProtection' 1 'DWord' 'Verificar ao abrir arquivos'
    Valor "$d\Real-Time Protection" 'DisableScanOnRealtimeEnable' 1 'DWord' 'Verificação ao ligar a proteção'
    Valor "$d\Real-Time Protection" 'DisableIOAVProtection' 1 'DWord' 'Verificar downloads'
    Valor "$d\Spynet" 'SpynetReporting' 0 'DWord' 'Envio de amostras para a Microsoft'
    Valor "$d\Spynet" 'SubmitSamplesConsent' 2 'DWord' 'Nunca enviar amostras'
    foreach ($n in 'Windows Defender Scheduled Scan', 'Windows Defender Cache Maintenance', 'Windows Defender Cleanup', 'Windows Defender Verification') {
        Tarefa "\Microsoft\Windows\Windows Defender\$n" 'Verificações agendadas do Defender' -Tolerante
    }
    Valor 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'SmartScreenEnabled' 'Off' 'String' 'SmartScreen (sem internet ele só atrasa ao abrir programas)'
}

function Parte-Pesquisar([bool]$tirarDeVez) {
    Titulo 'Cortana e o "Pesquisar" (SearchUI)'
    $s = "$Politicas\Windows Search"
    Valor $s 'AllowCortana' 0 'DWord' 'Desligar a Cortana'
    Valor $s 'DisableWebSearch' 1 'DWord' 'Pesquisa na internet'
    Valor $s 'ConnectedSearchUseWeb' 0 'DWord' 'Resultados da internet na pesquisa'
    Valor $s 'AllowSearchToUseLocation' 0 'DWord' 'Pesquisa usar a localização'
    Valor "$TudoDoUsuario\Search" 'SearchboxTaskbarMode' 0 'DWord' 'Esconder a caixa de pesquisa da barra de tarefas'
    Valor "$TudoDoUsuario\Search" 'BingSearchEnabled' 0 'DWord' 'Pesquisa no Bing'
    if (-not $tirarDeVez) { Pulado 'O "Pesquisar" continua no Windows (você escolheu não tirar de vez)'; return }
    # O SearchUI.exe volta sozinho quando é fechado: o único jeito no 1511 é ele não existir com esse nome.
    $pastaCortana = "$env:SystemRoot\SystemApps\Microsoft.Windows.Cortana_cw5n1h2txyewy"
    $exe = Join-Path $pastaCortana 'SearchUI.exe'
    $novo = "$exe.bcfichas"
    if (-not (Test-Path $exe)) {
        if (Test-Path $novo) { Pulado 'O "Pesquisar" já estava tirado' } else { Pulado 'Não achei o SearchUI.exe neste Windows' }
        return
    }
    if ($Simular) { Feito 'Tirar o "Pesquisar" de vez (renomear SearchUI.exe)'; return }
    try {
        if (-not (Test-Path $Pasta)) { New-Item -ItemType Directory -Path $Pasta -Force | Out-Null }
        $acl = Join-Path $Pasta 'searchui-permissoes.txt'
        [void](Rodar 'icacls.exe' @($exe, '/save', $acl))
        if ($LASTEXITCODE -ne 0) { throw 'não consegui guardar as permissões do arquivo' }
        Lembrar ([pscustomobject]@{ chave = "arquivo|$exe"; tipo = 'arquivo'; de = $exe; para = $novo; permissoes = $acl })
        [void](Rodar 'takeown.exe' @('/f', $exe, '/a'))
        [void](Rodar 'icacls.exe' @($exe, '/grant', '*S-1-5-32-544:F'))
        Get-Process -Name 'SearchUI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        Rename-Item -Path $exe -NewName (Split-Path $novo -Leaf)
        Get-Process -Name 'SearchUI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Feito 'Tirar o "Pesquisar" de vez (SearchUI.exe renomeado)'
    } catch {
        Falhou "Tirar o ""Pesquisar"": $($_.Exception.Message)"
        Esquecer "arquivo|$exe"
    }
}

$AppsParaTirar = @(
    'Microsoft.3DBuilder', 'Microsoft.Appconnector', 'Microsoft.BingFinance', 'Microsoft.BingNews',
    'Microsoft.BingSports', 'Microsoft.BingWeather', 'Microsoft.CommsPhone', 'Microsoft.Getstarted',
    'Microsoft.Messaging', 'Microsoft.MicrosoftOfficeHub', 'Microsoft.MicrosoftSolitaireCollection',
    'Microsoft.Office.OneNote', 'Microsoft.Office.Sway', 'Microsoft.People', 'Microsoft.SkypeApp',
    'Microsoft.WindowsAlarms', 'Microsoft.WindowsCamera', 'microsoft.windowscommunicationsapps',
    'Microsoft.WindowsMaps', 'Microsoft.WindowsPhone', 'Microsoft.WindowsSoundRecorder', 'Microsoft.XboxApp',
    'Microsoft.ZuneMusic', 'Microsoft.ZuneVideo', 'king.com.*', '9E2F88E3.Twitter', '*Flipboard*',
    'Microsoft.MinecraftUWP'
)

function Parte-Apps([bool]$tirar) {
    Titulo 'Aplicativos que vêm com o Windows (jogos, Xbox, notícias, Skype...)'
    if (-not $tirar) { Pulado 'Você escolheu manter os aplicativos'; return }
    # Nunca tira a Loja, a Calculadora, as bibliotecas (frameworks) nem os aplicativos do sistema.
    foreach ($nome in $AppsParaTirar) {
        $pacotes = @(Get-AppxPackage -Name $nome -ErrorAction SilentlyContinue)
        foreach ($p in $pacotes) {
            if ($p.IsFramework -or $p.Name -like '*Store*' -or $p.Name -like '*Calculator*') { continue }
            if ($Simular) { Feito "Desinstalar $($p.Name)"; continue }
            try {
                Lembrar ([pscustomobject]@{ chave = "appx|$($p.Name)"; tipo = 'appx'; nome = $p.Name; pacote = $p.PackageFullName })
                Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop
                Feito "Desinstalado: $($p.Name)"
            } catch { Falhou "Desinstalar $($p.Name): $($_.Exception.Message)" }
        }
    }
}

function Parte-OneDrive {
    Titulo 'OneDrive (abre junto com o Windows mesmo sem internet)'
    $setup = "$env:SystemRoot\System32\OneDriveSetup.exe"
    if (-not (Test-Path $setup)) { $setup = "$env:SystemRoot\SysWOW64\OneDriveSetup.exe" }
    $instalado = (Test-Path "$env:LOCALAPPDATA\Microsoft\OneDrive\OneDrive.exe") -or [bool](Ler-Valor "$TudoDoUsuario\Run" 'OneDrive')
    if ($instalado -and (Test-Path $setup)) {
        if ($Simular) { Feito 'Desinstalar o OneDrive' }
        else {
            try {
                Lembrar ([pscustomobject]@{ chave = 'onedrive'; tipo = 'onedrive'; setup = $setup })
                Get-Process -Name 'OneDrive' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                $p = Start-Process -FilePath $setup -ArgumentList '/uninstall' -Wait -PassThru
                Feito "Desinstalar o OneDrive (código $($p.ExitCode))"
            } catch { Falhou "Desinstalar o OneDrive: $($_.Exception.Message)" }
        }
    } else { Pulado 'OneDrive não está instalado para este usuário' }
    Tirar-Valor "$TudoDoUsuario\Run" 'OneDrive' 'Tirar o OneDrive do início do Windows'
    Valor "$Politicas\OneDrive" 'DisableFileSyncNGSC' 1 'DWord' 'Não deixar o OneDrive voltar'
    $clsid = 'HKLM:\SOFTWARE\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}'
    if (Test-Path $clsid) { Valor $clsid 'System.IsPinnedToNameSpaceTree' 0 'DWord' 'Tirar o OneDrive da lateral do Explorador' }
}

function Parte-Usuario {
    Titulo 'Dicas, sugestões, notificações e aplicativos em segundo plano'
    Valor "$Politicas\CloudContent" 'DisableWindowsConsumerFeatures' 1 'DWord' 'Não instalar jogos e aplicativos sugeridos'
    Valor "$Politicas\CloudContent" 'DisableSoftLanding' 1 'DWord' 'Dicas do Windows'
    $cdm = "$TudoDoUsuario\ContentDeliveryManager"
    foreach ($n in 'ContentDeliveryAllowed', 'SilentInstalledAppsEnabled', 'SystemPaneSuggestionsEnabled', 'SoftLandingEnabled',
        'PreInstalledAppsEnabled', 'OemPreInstalledAppsEnabled', 'RotatingLockScreenEnabled', 'RotatingLockScreenOverlayEnabled') {
        Valor $cdm $n 0 'DWord' "Sugestões e propagandas ($n)"
    }
    Valor "$TudoDoUsuario\PushNotifications" 'ToastEnabled' 0 'DWord' 'Notificações dos aplicativos por cima do caixa'
    Valor "$Politicas\Personalization" 'NoLockScreen' 1 'DWord' 'Sem tela de bloqueio (ao ligar a tela volta direto para o BC Fichas)'
    Valor "$TudoDoUsuario\Explorer\AutoplayHandlers" 'DisableAutoplay' 1 'DWord' 'Não abrir janela ao ligar pendrive'
    Valor 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoDriveTypeAutoRun' 255 'DWord' 'Não executar nada sozinho de pendrive'
    Valor 'HKLM:\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config' 'AutoConnectAllowedOEM' 0 'DWord' 'Não conectar sozinho em Wi-Fi aberto (Wi-Fi Sense)'
    # Aplicativos em segundo plano: todos menos os do sistema (terminam em _cw5n1h2txyewy)
    $bg = "$TudoDoUsuario\BackgroundAccessApplications"
    if (Test-Path $bg) {
        foreach ($k in @(Get-ChildItem -Path $bg -ErrorAction SilentlyContinue)) {
            if ($k.PSChildName -like '*_cw5n1h2txyewy') { continue }
            $c = "$bg\$($k.PSChildName)"
            Valor $c 'Disabled' 1 'DWord' "Segundo plano: $($k.PSChildName.Split('_')[0])"
            Valor $c 'DisabledByUser' 1 'DWord' "Segundo plano: $($k.PSChildName.Split('_')[0]) (usuário)"
        }
    }
}

function Parte-Visual {
    Titulo 'Efeitos visuais no modo desempenho (animações, sombras e transparência gastam o processador)'
    Valor "$TudoDoUsuario\Explorer\VisualEffects" 'VisualFXSetting' 3 'DWord' 'Efeitos visuais personalizados'
    Valor 'HKCU:\Control Panel\Desktop' 'UserPreferencesMask' @(0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00) 'Binary' 'Sem animações, sombras e esmaecimentos (só a letra suavizada)'
    Valor 'HKCU:\Control Panel\Desktop' 'DragFullWindows' '0' 'String' 'Não desenhar a janela inteira ao arrastar'
    Valor 'HKCU:\Control Panel\Desktop\WindowMetrics' 'MinAnimate' '0' 'String' 'Sem animação ao abrir e minimizar janelas'
    Valor "$TudoDoUsuario\Explorer\Advanced" 'TaskbarAnimations' 0 'DWord' 'Sem animações na barra de tarefas'
    Valor "$TudoDoUsuario\Explorer\Advanced" 'ListviewAlphaSelect' 0 'DWord' 'Seleção transparente'
    Valor "$TudoDoUsuario\Explorer\Advanced" 'ListviewShadow' 0 'DWord' 'Sombra nos nomes dos ícones'
    Valor 'HKCU:\Software\Microsoft\Windows\DWM' 'EnableAeroPeek' 0 'DWord' 'Espiar a área de trabalho'
    Valor "$TudoDoUsuario\Themes\Personalize" 'EnableTransparency' 0 'DWord' 'Sem transparência no Iniciar e na barra de tarefas'
}

function Parte-Inicio {
    Titulo 'Início do Windows'
    Valor "$TudoDoUsuario\Explorer\Serialize" 'StartupDelayInMSec' 0 'DWord' 'BC Fichas abre logo, sem a espera de 10 segundos do Windows'
    # Se alguém desmarcou o BC Fichas no Gerenciador de Tarefas > Inicializar, ele não abriria sozinho.
    Tirar-Valor "$TudoDoUsuario\Explorer\StartupApproved\Run" 'BCFichas' 'Garantir que o BC Fichas continua abrindo sozinho'
    $exe = Achar-BCFichas
    if ($exe) {
        # Arquivos baixados de outro computador ficam "bloqueados": sem internet o Windows demora para abrir.
        if ($Simular) { Feito "Desbloquear os arquivos do BC Fichas ($(Split-Path $exe))" }
        else {
            try {
                Get-ChildItem -Path (Split-Path $exe) -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
                Feito "Desbloquear os arquivos do BC Fichas ($(Split-Path $exe))"
            } catch { Falhou "Desbloquear os arquivos do BC Fichas: $($_.Exception.Message)" }
        }
    } else { Pulado 'Não achei o BC Fichas neste tablet (abra o programa uma vez antes)' }
    Escrever '   O que abre junto com o Windows (só para conferir; o script não mexe nestes):' 'DarkGray'
    foreach ($c in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') {
        if (-not (Test-Path $c)) { continue }
        $k = Get-Item $c
        foreach ($n in $k.GetValueNames()) { Escrever "     $n = $($k.GetValue($n))" 'DarkGray' }
    }
}

function Parte-Relogio {
    Titulo 'Relógio (horário de verão)'
    # O Brasil acabou com o horário de verão em 2019, mas o Windows 1511 sem internet ainda adianta o relógio
    # em outubro: os horários das fichas e dos relatórios ficariam uma hora errados.
    $fuso = ''
    try { $fuso = (Rodar 'tzutil.exe' @('/g')).Trim() } catch { }
    $comVerao = @('E. South America Standard Time', 'Central Brazilian Standard Time')
    if ($comVerao -notcontains $fuso) { Pulado "Fuso '$fuso' não tem o horário de verão antigo do Brasil"; return }
    if ($Simular) { Feito "Tirar o horário de verão do fuso ($fuso)"; return }
    try {
        Lembrar ([pscustomobject]@{ chave = 'fuso'; tipo = 'fuso'; fuso = $fuso })
        [void](Rodar 'tzutil.exe' @('/s', "${fuso}_dstoff"))
        if ($LASTEXITCODE -ne 0) { throw "tzutil terminou com código $LASTEXITCODE" }
        Feito "Tirar o horário de verão do fuso ($fuso). Confira se a hora do tablet está certa."
    } catch { Falhou "Horário de verão: $($_.Exception.Message)"; Esquecer 'fuso' }
}

function Parte-Disco([bool]$tirarHibernacao) {
    Titulo 'Disco e energia'
    Valor 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' 'CrashDumpEnabled' 3 'DWord' 'Se o Windows travar, gravar só um relatório pequeno (não um arquivo do tamanho da memória)'
    if (-not $tirarHibernacao) { Pulado 'Hibernação: você escolheu manter'; return }
    $hiber = Ler-Valor 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled'
    $temArquivo = Test-Path "$env:SystemDrive\hiberfil.sys"
    if (-not $temArquivo -and $hiber -and [int]$hiber.valor -eq 0) { Pulado 'Hibernação já estava desligada'; return }
    if (-not $temArquivo -and -not $hiber) { Pulado 'Este Windows não usa hibernação'; return }
    if ($Simular) { Feito 'Desligar a hibernação (apaga o hiberfil.sys, uns 700 MB)'; return }
    try {
        $bateria = Bateria-Critica
        Lembrar ([pscustomobject]@{ chave = 'hibernacao'; tipo = 'hibernacao' })
        [void](Rodar 'powercfg.exe' @('/h', 'off'))
        if ($LASTEXITCODE -ne 0) { throw "powercfg terminou com código $LASTEXITCODE" }
        Feito 'Desligar a hibernação (apaga o hiberfil.sys, uns 700 MB)'
        # Sem hibernação, com a bateria no fim o tablet desliga (e não tenta hibernar).
        if ($bateria) {
            Lembrar ([pscustomobject]@{ chave = 'bateria'; tipo = 'bateria'; ac = $bateria[0]; dc = $bateria[1] })
            [void](Rodar 'powercfg.exe' @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_BATTERY', 'BATACTIONCRIT', '3'))
            [void](Rodar 'powercfg.exe' @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_BATTERY', 'BATACTIONCRIT', '3'))
            [void](Rodar 'powercfg.exe' @('/setactive', 'SCHEME_CURRENT'))
            Feito 'Bateria no fim: desligar o tablet'
        }
    } catch { Falhou "Hibernação: $($_.Exception.Message)" }
}

# Ação da bateria crítica de agora: @(na tomada, na bateria), ou $null se não deu para ler.
function Bateria-Critica {
    try {
        $saida = Rodar 'powercfg.exe' @('/q', 'SCHEME_CURRENT', 'SUB_BATTERY', 'BATACTIONCRIT')
        $valores = @([regex]::Matches($saida, '0x([0-9a-fA-F]{8})') | ForEach-Object { [Convert]::ToInt32($_.Groups[1].Value, 16) })
        if ($valores.Count -ge 2) { return @($valores[$valores.Count - 2], $valores[$valores.Count - 1]) }
    } catch { }
    return $null
}

function Conferir-Memoria {
    Titulo 'Conferindo o que precisa continuar ligado'
    $sysmain = Ler-Valor 'HKLM:\SYSTEM\CurrentControlSet\Services\SysMain' 'Start'
    if ($sysmain -and [int]$sysmain.valor -eq 4) {
        Aviso 'O SysMain (Superfetch) está desativado. No Windows 1511 ele também faz a compressão da memória, que ajuda muito com 1 GB. O script religa.'
        Servico-Religar 'SysMain'
    } else { Pulado 'SysMain (compressão da memória) ligado' }
    $pag = $null
    try { $pag = (Get-CimInstance -ClassName Win32_ComputerSystem).AutomaticManagedPagefile } catch { }
    if ($pag -eq $false) { Aviso 'O arquivo de paginação não está no automático. Com 1 GB de RAM, deixe "Gerenciar automaticamente" (Sistema > Configurações avançadas > Desempenho > Memória virtual).' }
    else { Pulado 'Arquivo de paginação no automático' }
}

function Servico-Religar([string]$nome) {
    $caminho = "HKLM:\SYSTEM\CurrentControlSet\Services\$nome"
    if ($Simular) { Feito "Religar $nome"; return }
    try {
        Lembrar ([pscustomobject]@{ chave = "svc|$nome"; tipo = 'servico'; nome = $nome; start = 4; atrasado = $null })
        Set-ItemProperty -Path $caminho -Name 'Start' -Value 2 -Type DWord
        Start-Service -Name $nome -ErrorAction SilentlyContinue
        Feito "Religar $nome"
    } catch { Falhou "Religar $($nome): $($_.Exception.Message)" }
}

function Deixar-Leve {
    Escrever 'BC Fichas - deixar o Windows leve' 'White'
    if ($Simular) { Escrever 'MODO SIMULAÇÃO: nada vai ser mudado, só mostrado.' 'Yellow' }
    Escrever "Memória agora: $(Memoria)"
    Escrever 'Programas que mais usam memória agora:'
    Maiores-Processos
    Conferir-Usuario
    Carregar-Estado

    Escrever ''
    Escrever 'Algumas perguntas (Enter = resposta recomendada):' 'White'
    $tirarPesquisar = Perguntar 'Tirar o "Pesquisar" (Cortana) de vez? O Iniciar fica sem a busca.' $true
    $tirarApps = Perguntar 'Desinstalar os aplicativos que vêm com o Windows (jogos, Xbox, notícias, Skype...)? Sem internet não dá para reinstalar.' $true
    $desligarDefender = Perguntar 'Desligar o Windows Defender (antivírus)? O tablet fica sem antivírus.' $true
    $tirarHibernacao = Perguntar 'Desligar a hibernação? Libera uns 700 MB do disco e a imagem do tablet fica menor.' $true

    Parte-Telemetria
    Parte-WindowsUpdate
    Parte-Servicos
    Parte-Defender $desligarDefender
    Parte-Pesquisar $tirarPesquisar
    Parte-OneDrive
    Parte-Apps $tirarApps
    Parte-Usuario
    Parte-Visual
    Parte-Inicio
    Parte-Relogio
    Parte-Disco $tirarHibernacao
    Conferir-Memoria
}

# ---------------------------------------------------------------- modo caixa (o Windows abre só o BC Fichas)

$ChaveShell = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'

$ScriptCaixa = @'
' BC Fichas - modo caixa: o Windows abre so o BC Fichas, sem a area de trabalho.
' Se o programa fechar por erro, ele abre de novo. Se fechar pelo "Sair do programa", abre a area de trabalho
' (para manutencao). Para manutencao sem fechar o programa: crie o arquivo manutencao.txt nesta pasta.
Option Explicit
Dim sh, fso, pasta, exe, rc, t0, falhas, wmi, lista
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
pasta = fso.GetParentFolderName(WScript.ScriptFullName)
exe = pasta & "\BCFichas.exe"
sh.CurrentDirectory = pasta
falhas = 0
Do
  If fso.FileExists(pasta & "\manutencao.txt") Then Exit Do
  Set wmi = GetObject("winmgmts:\\.\root\cimv2")
  Set lista = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='BCFichas.exe'")
  If lista.Count > 0 Then
    WScript.Sleep 5000
  Else
    t0 = Timer
    rc = sh.Run("""" & exe & """", 1, True)
    If rc = 0 Then Exit Do
    If Abs(Timer - t0) < 30 Then falhas = falhas + 1 Else falhas = 0
    If falhas >= 5 Then Exit Do
    WScript.Sleep 3000
  End If
Loop
sh.Run "explorer.exe", 1, False
'@

function Entrar-No-Caixa {
    Escrever 'BC Fichas - modo caixa' 'White'
    Conferir-Usuario
    Carregar-Estado
    $exe = Achar-BCFichas
    if (-not $exe) {
        if ($SemPerguntar) { Falhou 'Não achei o BCFichas.exe. Abra o BC Fichas uma vez antes.'; return }
        $exe = Read-Host 'Não achei o BC Fichas. Digite o caminho do BCFichas.exe (ex.: C:\BCFichas\BCFichas.exe)'
        if (-not (Test-Path $exe)) { Falhou "Não achei $exe"; return }
    }
    $vbs = Join-Path (Split-Path $exe) 'modo-caixa.vbs'
    Titulo 'Ligando o modo caixa'
    if ($Simular) { Feito "Criar $vbs" }
    else {
        try { [System.IO.File]::WriteAllText($vbs, $ScriptCaixa.Replace("`r`n", "`n").Replace("`n", "`r`n"), [System.Text.Encoding]::ASCII); Feito "Criar $vbs" }
        catch { Falhou "Criar $($vbs): $($_.Exception.Message)"; return }
    }
    $wscript = "$env:SystemRoot\System32\wscript.exe"
    Valor $ChaveShell 'Shell' "$wscript //B //NoLogo `"$vbs`"" 'String' 'O Windows abre o BC Fichas no lugar da área de trabalho'
    # Pelo UltraVNC, "Enviar Ctrl+Alt+Del" precisa disto (o instalador do VNC normalmente já põe).
    if (-not (Ler-Valor 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'SoftwareSASGeneration')) {
        Valor 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'SoftwareSASGeneration' 3 'DWord' 'Ctrl+Alt+Del pelo VNC'
    }
    Escrever ''
    Escrever 'Pronto. Reinicie o tablet: ele vai abrir só o BC Fichas.' 'Green'
    Escrever 'Para manutenção: no BC Fichas, Menu > Sair do programa abre a área de trabalho.'
    Escrever 'Ou: Ctrl+Alt+Del > Gerenciador de Tarefas > Arquivo > Executar nova tarefa > explorer.exe.'
    Escrever 'Para voltar ao normal: 4-Sair-do-modo-caixa.cmd e reinicie.'
}

function Sair-Do-Caixa {
    Escrever 'BC Fichas - sair do modo caixa' 'White'
    Carregar-Estado
    $chave = "reg|$ChaveShell|Shell"
    $item = @($script:Estado.ToArray() | Where-Object { $_.chave -eq $chave }) | Select-Object -First 1
    Titulo 'Voltando a área de trabalho normal'
    if ($item) {
        if ($Simular) { Feito 'Voltar a área de trabalho'; return }
        try {
            if ($item.existia) { Gravar-Valor $ChaveShell 'Shell' $item.valor $item.tipoValor }
            elseif (Ler-Valor $ChaveShell 'Shell') { Remove-ItemProperty -Path $ChaveShell -Name 'Shell' -Force }
            Esquecer $chave
            Feito 'Voltar a área de trabalho'
        } catch { Falhou "Voltar a área de trabalho: $($_.Exception.Message)" }
    } elseif (Ler-Valor $ChaveShell 'Shell') {
        if ($Simular) { Feito 'Voltar a área de trabalho'; return }
        try { Remove-ItemProperty -Path $ChaveShell -Name 'Shell' -Force; Feito 'Voltar a área de trabalho' }
        catch { Falhou "Voltar a área de trabalho: $($_.Exception.Message)" }
    } else { Pulado 'O modo caixa não estava ligado' }
    Escrever 'Reinicie o tablet para a área de trabalho voltar.' 'Green'
}

# ---------------------------------------------------------------- desfazer

function Desfazer-Tudo {
    Escrever 'BC Fichas - desfazer o Windows leve' 'White'
    Carregar-Estado
    if ($script:Estado.Count -eq 0) { Escrever 'Nada para desfazer: este script não mudou nada neste Windows (ou já foi desfeito).' 'Yellow'; return }
    $itens = @($script:Estado.ToArray())
    [array]::Reverse($itens)
    Titulo "Voltando $($itens.Count) mudança(s) ao que era antes"
    $naoVolta = @()
    foreach ($item in $itens) {
        try {
            switch ($item.tipo) {
                'registro' {
                    if (-not $Simular) {
                        if ($item.existia) { Gravar-Valor $item.caminho $item.nome $item.valor $item.tipoValor }
                        elseif (Ler-Valor $item.caminho $item.nome) { Remove-ItemProperty -Path $item.caminho -Name $item.nome -Force }
                    }
                    Feito "registro $($item.nome)"
                }
                'servico' {
                    $caminho = "HKLM:\SYSTEM\CurrentControlSet\Services\$($item.nome)"
                    if (-not $Simular -and (Test-Path $caminho)) {
                        if ($null -ne $item.start) { Set-ItemProperty -Path $caminho -Name 'Start' -Value ([int]$item.start) -Type DWord }
                        if ($null -ne $item.atrasado) { Set-ItemProperty -Path $caminho -Name 'DelayedAutostart' -Value ([int]$item.atrasado) -Type DWord }
                    }
                    Feito "serviço $($item.nome)"
                }
                'tarefa' {
                    $t = Achar-Tarefa $item.caminho
                    if (-not $t) { Pulado "tarefa $($item.caminho) não existe mais"; break }
                    if (-not $Simular) { $t.Enabled = [bool]$item.ativa }
                    Feito "tarefa $($item.caminho)"
                }
                'arquivo' {
                    if (-not $Simular) {
                        if ((Test-Path $item.para) -and -not (Test-Path $item.de)) { Rename-Item -Path $item.para -NewName (Split-Path $item.de -Leaf) }
                        if (Test-Path $item.permissoes) {
                            [void](Rodar 'icacls.exe' @((Split-Path $item.de), '/restore', $item.permissoes))
                            [void](Rodar 'icacls.exe' @($item.de, '/setowner', 'NT SERVICE\TrustedInstaller'))
                        }
                    }
                    Feito "arquivo $(Split-Path $item.de -Leaf)"
                }
                'fuso' {
                    if (-not $Simular) { [void](Rodar 'tzutil.exe' @('/s', $item.fuso)) }
                    Feito "fuso horário $($item.fuso)"
                }
                'hibernacao' {
                    if (-not $Simular) { [void](Rodar 'powercfg.exe' @('/h', 'on')) }
                    Feito 'hibernação'
                }
                'bateria' {
                    if (-not $Simular) {
                        [void](Rodar 'powercfg.exe' @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_BATTERY', 'BATACTIONCRIT', [string]$item.ac))
                        [void](Rodar 'powercfg.exe' @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_BATTERY', 'BATACTIONCRIT', [string]$item.dc))
                        [void](Rodar 'powercfg.exe' @('/setactive', 'SCHEME_CURRENT'))
                    }
                    Feito 'bateria no fim'
                }
                'onedrive' {
                    if (-not $Simular -and (Test-Path $item.setup)) { Start-Process -FilePath $item.setup | Out-Null }
                    Feito 'OneDrive (instalando de novo)'
                }
                'appx' { $naoVolta += $item.nome; Pulado "$($item.nome): aplicativo desinstalado não volta sem internet (só pela Loja)" }
                default { Pulado "não sei desfazer '$($item.tipo)'" }
            }
        } catch { Falhou "$($item.chave): $($_.Exception.Message)" }
    }
    if (-not $Simular -and $script:Falhas -eq 0) {
        $guardado = Join-Path $Pasta ("desfeito-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
        Move-Item -Path $ArquivoEstado -Destination $guardado -Force
        Escrever "O que foi desfeito ficou guardado em $guardado" 'DarkGray'
    }
    if ($naoVolta.Count -gt 0) { Aviso "$($naoVolta.Count) aplicativo(s) desinstalado(s) não voltam sem internet." }
}

# ---------------------------------------------------------------- começo

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Host 'Este script precisa rodar como administrador. Use os arquivos .cmd da pasta: eles pedem a permissão.' -ForegroundColor Red
    exit 2
}

try {
    $so = Get-CimInstance -ClassName Win32_OperatingSystem
    Escrever "$($so.Caption) - versão $($so.Version) - $($so.OSArchitecture)" 'DarkGray'
    if ($so.BuildNumber -ne '10586') { Aviso "Este script foi feito para o Windows 10 1511 (10586). Este é o $($so.BuildNumber): quase tudo vale igual, mas confira o tablet depois." }
} catch { }

switch ($Acao) {
    'Leve' { Deixar-Leve }
    'Desfazer' { Desfazer-Tudo }
    'Caixa' { Entrar-No-Caixa }
    'SairDoCaixa' { Sair-Do-Caixa }
}

Escrever ''
if ($Simular) { Escrever "Simulação: $($script:Mudancas) mudança(s) seriam feitas. Nada foi mudado." 'Yellow' }
else { Escrever "Pronto: $($script:Mudancas) mudança(s)." 'Green' }
if ($script:Falhas -gt 0) { Escrever "$($script:Falhas) coisa(s) não deram certo (veja as linhas com !! acima e o arquivo $Diario)." 'Red' }
foreach ($a in $script:Avisos) { Escrever "Atenção: $a" 'Yellow' }
if (-not $Simular -and $Acao -eq 'Leve') {
    Escrever ''
    Escrever 'Reinicie o tablet para valer tudo. Depois, confira a memória no Gerenciador de Tarefas > Desempenho.' 'White'
    Escrever "Para voltar como era: 2-Desfazer.cmd (o que foi mudado está em $ArquivoEstado)." 'White'
}
if ($script:Falhas -gt 0) { exit 1 }
exit 0
