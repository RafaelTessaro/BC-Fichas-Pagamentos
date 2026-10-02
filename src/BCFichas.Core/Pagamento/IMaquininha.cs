namespace BCFichas.Core.Pagamento;

/// <summary>Pedido de cobrança enviado para a maquininha.</summary>
/// <param name="Id">Identificador único (usado para consultar depois se a conexão cair).</param>
public sealed record Cobranca(string Id, long ValorCentavos, FormaPagamento Forma, string Descricao);

public sealed record ResultadoCobranca(bool Aprovado, string Mensagem, string? Autorizacao = null);

/// <summary>
/// Conexão com a maquininha de cartão. Hoje existem a maquininha separada (o operador cobra nela e confirma
/// na tela) e o simulador; a ligação por Bluetooth (app ponte) vai implementar esta mesma interface.
/// </summary>
public interface IMaquininha
{
    string Nome { get; }

    /// <summary>Envia o valor para a maquininha e espera o cliente pagar.</summary>
    Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar);

    /// <summary>Pergunta o resultado de uma cobrança já enviada. Nulo se a maquininha não souber dizer.</summary>
    Task<ResultadoCobranca?> ConsultarAsync(string id, CancellationToken cancelar);
}

/// <summary>
/// Maquininha em que quem decide é o operador: ele toca em "aprovado" ou "não aprovou" na tela.
/// </summary>
public abstract class MaquininhaManual : IMaquininha
{
    private TaskCompletionSource<ResultadoCobranca>? _decisao;

    public abstract string Nome { get; }

    /// <summary>Texto do botão que confirma o pagamento.</summary>
    public abstract string TextoAprovar { get; }

    /// <summary>Texto do botão de pagamento não aprovado.</summary>
    public abstract string TextoRecusar { get; }

    public bool AguardandoDecisao => _decisao is { Task.IsCompleted: false };

    public async Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento,
        CancellationToken cancelar)
    {
        var decisao = new TaskCompletionSource<ResultadoCobranca>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decisao = decisao;
        using var registro = cancelar.Register(() => decisao.TrySetCanceled(cancelar));
        try
        {
            await Preparar(cobranca, andamento, cancelar);
            return await decisao.Task;
        }
        finally
        {
            _decisao = null;
        }
    }

    public virtual Task<ResultadoCobranca?> ConsultarAsync(string id, CancellationToken cancelar) =>
        Task.FromResult<ResultadoCobranca?>(null);

    public void Aprovar(string? autorizacao = null) =>
        _decisao?.TrySetResult(new ResultadoCobranca(true, "Pagamento aprovado", autorizacao));

    public void Recusar(string motivo) => _decisao?.TrySetResult(new ResultadoCobranca(false, motivo));

    /// <summary>Mostra o que o operador tem de fazer enquanto espera a decisão.</summary>
    protected abstract Task Preparar(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar);
}

/// <summary>
/// Maquininha usada à parte, sem ligação com o programa (o jeito de hoje): o operador passa o valor na
/// maquininha e, quando ela aprova, confirma aqui. A forma de pagamento fica registrada para o relatório.
/// </summary>
public sealed class MaquininhaSeparada : MaquininhaManual
{
    public override string Nome => "separada";
    public override string TextoAprovar => "APROVADO NA MAQUININHA";
    public override string TextoRecusar => "Não aprovou";

    protected override Task Preparar(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar)
    {
        andamento?.Report(cobranca.Forma == FormaPagamento.Pix
            ? $"Gere o PIX de {Dinheiro.Formatar(cobranca.ValorCentavos)} na maquininha"
            : $"Passe {Dinheiro.Formatar(cobranca.ValorCentavos)} no {Nomes.De(cobranca.Forma).ToUpperInvariant()} na maquininha");
        return Task.CompletedTask;
    }
}
