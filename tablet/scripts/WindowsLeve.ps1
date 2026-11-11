<#
    BC Fichas - Windows leve para o tablet do caixa (Windows 10 1511, 32 bits, 1 GB de RAM)

    Uso (os arquivos .cmd da pasta de cima chamam este script já como administrador):
      WindowsLeve.ps1 -Acao Leve          deixa o Windows leve (guarda antes como estava cada coisa)
      WindowsLeve.ps1 -Acao Desfazer      volta tudo o que dá para voltar ao que era antes
      WindowsLeve.ps1 -Acao Caixa         modo caixa: o Windows abre só o BC Fichas, sem a área de trabalho
      WindowsLeve.ps1 -Acao SairDoCaixa   volta a área de trabalho normal
    Opções:
      -Simular        só mostra o que faria, sem mudar nada
      -SemPerguntar   não pergunta nada (para rodar sem ninguém olhando)
      -Pasta <pasta>  onde guardar o que é preciso para desfazer (padrão: C:\ProgramData\BCFichasWindowsLeve)

    Feito para o Windows PowerShell 5.0 do Windows 10 1511: nada de recurso mais novo.
    O arquivo é gravado em UTF-8 com BOM (sem o BOM o PowerShell 5 lê os acentos errado).
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

# ---------------------------------------------------------------- como era antes (para desfazer)
# Cada coisa mudada é guardada UMA vez, com o valor de antes da primeira mudança: rodar o script de novo não
# troca o "antes" pelo que já tinha sido mudado.

$script:Estado = New-Object System.Collections.ArrayList
$script:Chaves = @{}

function Carregar-Estado {
    if (Test-Path $ArquivoEstado) {
        $lidos = Get-Content -Path $ArquivoEstado -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($item in @($lidos)) {
            [void]$script:Estado.Add($item)
            $script:Chaves[$item.chave] = $true
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
    New-ItemProperty -Path $caminho -Name $nome -Value $valor -PropertyType $tipo -Force | Out-Null
}

# Muda um valor do registro e guarda o que tinha antes (ou que não existia).
function Valor([string]$caminho, [string]$nome, $valor, [string]$tipo = 'DWord', [string]$porque = '') {
    $antes = Ler-Valor $caminho $nome
    $descricao = if ($porque) { $porque } else { "$caminho\$nome = $valor" }
    if ($antes -and $antes.tipo -eq $tipo -and (($antes.valor -join ',') -eq (@($valor) -join ','))) {
        Pulado "$descricao (já estava assim)"
        return
    }
    if ($Simular) { Feito $descricao; return }
    try {
        Lembrar ([pscustomobject]@{
            chave = "reg|$caminho|$nome"; tipo = 'registro'; caminho = $caminho; nome = $nome
            existia = [bool]$antes; tipoValor = if ($antes) { $antes.tipo } else { $null }; valor = if ($antes) { $antes.valor } else { $null }
        })
        Gravar-Valor $caminho $nome $valor $tipo
        Feito $descricao
    } catch { Falhou "$descricao : $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- serviços

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

function Tarefa([string]$caminhoCompleto, [string]$porque) {
    $t = Achar-Tarefa $caminhoCompleto
    if (-not $t) { Pulado "$porque (tarefa $caminhoCompleto não existe)"; return }
    if (-not $t.Enabled) { Pulado "$porque (já estava desligada)"; return }
    if ($Simular) { Feito "$porque ($caminhoCompleto)"; return }
    try {
        Lembrar ([pscustomobject]@{ chave = "task|$caminhoCompleto"; tipo = 'tarefa'; caminho = $caminhoCompleto; ativa = $true })
        $t.Enabled = $false
        Feito "$porque ($caminhoCompleto)"
    } catch { Falhou "$porque ($caminhoCompleto): $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- desfazer

function Desfazer-Tudo {
    Carregar-Estado
    if ($script:Estado.Count -eq 0) { Escrever 'Nada para desfazer: este script não mudou nada neste Windows (ou já foi desfeito).' 'Yellow'; return }
    $itens = @($script:Estado.ToArray())
    [array]::Reverse($itens)
    Titulo "Voltando $($itens.Count) mudança(s) ao que era antes"
    foreach ($item in $itens) {
        try {
            switch ($item.tipo) {
                'registro' {
                    if ($Simular) { Feito "registro $($item.caminho)\$($item.nome)"; break }
                    if ($item.existia) { Gravar-Valor $item.caminho $item.nome $item.valor $item.tipoValor }
                    elseif ((Test-Path $item.caminho) -and ((Get-Item $item.caminho).GetValueNames() -contains $item.nome)) {
                        Remove-ItemProperty -Path $item.caminho -Name $item.nome -Force
                    }
                    Feito "registro $($item.caminho)\$($item.nome)"
                }
                'servico' {
                    $caminho = "HKLM:\SYSTEM\CurrentControlSet\Services\$($item.nome)"
                    if ($Simular) { Feito "serviço $($item.nome)"; break }
                    if ($null -ne $item.start) { Set-ItemProperty -Path $caminho -Name 'Start' -Value ([int]$item.start) -Type DWord }
                    if ($null -ne $item.atrasado) { Set-ItemProperty -Path $caminho -Name 'DelayedAutostart' -Value ([int]$item.atrasado) -Type DWord }
                    Feito "serviço $($item.nome)"
                }
                'tarefa' {
                    $t = Achar-Tarefa $item.caminho
                    if (-not $t) { Pulado "tarefa $($item.caminho) não existe mais"; break }
                    if (-not $Simular) { $t.Enabled = [bool]$item.ativa }
                    Feito "tarefa $($item.caminho)"
                }
                'pasta' {
                    if (-not $Simular) {
                        if ((Test-Path $item.para) -and -not (Test-Path $item.de)) { Rename-Item -Path $item.para -NewName (Split-Path $item.de -Leaf) }
                    }
                    Feito "pasta $($item.de)"
                }
                default { Pulado "não sei desfazer '$($item.tipo)'" }
            }
        } catch { Falhou "$($item.chave): $($_.Exception.Message)" }
    }
    if (-not $Simular -and $script:Falhas -eq 0) {
        $guardado = Join-Path $Pasta ("desfeito-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
        Move-Item -Path $ArquivoEstado -Destination $guardado -Force
        Escrever "O que foi desfeito ficou guardado em $guardado" 'DarkGray'
    }
}
