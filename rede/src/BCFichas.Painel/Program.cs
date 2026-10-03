using System.Diagnostics;
using BCFichas.Core;
using BCFichas.Painel;

// Painel da rede do evento. Aberto pelo caixa quando o painel está ligado nas configurações:
//   BCFichasPainel.exe --dados "C:\BCFichas\dados" --pai 1234
// Sai sozinho quando o caixa fecha ou quando o painel é desligado.

using var unico = new Mutex(true, "BCFichasPainel-instancia-unica", out var primeiro);
if (!primeiro) return 0;

string? Argumento(string nome)
{
    var i = Array.IndexOf(args, nome);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var pasta = Argumento("--dados") ?? Sistema.PastaDadosPadrao();
var porta = int.TryParse(Argumento("--porta"), out var p) ? p : Rede.PortaPadrao;
Registro.Pasta = pasta;

try
{
    // Abaixo do caixa: a venda sempre tem a preferência do processador
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
}
catch (Exception)
{
    // Sem permissão para mudar a prioridade: segue normal
}

Process? pai = null;
if (int.TryParse(Argumento("--pai"), out var pid))
{
    try
    {
        pai = Process.GetProcessById(pid);
    }
    catch (ArgumentException)
    {
        return 0; // o caixa já fechou
    }
}

Servidor servidor;
try
{
    servidor = await Servidor.Iniciar(pasta, porta);
}
catch (Exception e)
{
    // Porta ocupada, rede desligada: o caixa continua funcionando sem o painel
    Registro.Erro("Abrir o painel", e);
    return 1;
}

await using (servidor)
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (pai is { HasExited: true }) break;
        if (!servidor.Leitor.Config.PainelAtivo) break;
    }
}
return 0;
