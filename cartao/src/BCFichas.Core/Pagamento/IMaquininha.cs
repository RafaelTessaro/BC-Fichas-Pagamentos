namespace BCFichas.Core.Pagamento;

/// <summary>Pedido de cobrança enviado para a maquininha.</summary>
/// <param name="Id">Identificador único (usado para consultar depois se a conexão cair).</param>
/// <param name="Referencia">Código da venda que aparece no PagBank (até 10 letras e números).</param>
public sealed record Cobranca(string Id, long ValorCentavos, FormaPagamento Forma, string Descricao, string? Referencia = null);

public sealed record ResultadoCobranca(bool Aprovado, string Mensagem, string? Autorizacao = null);

/// <summary>
/// Conexão com a maquininha de cartão: a maquininha separada (o operador cobra nela e confirma na tela), o simulador
/// e a Moderninha Smart 2 do PagBank ligada por Bluetooth (<see cref="MaquininhaPagBank"/>).
/// </summary>
public interface IMaquininha
{
    string Nome { get; }

    /// <summary>Envia o valor para a maquininha e espera o cliente pagar.</summary>
    Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar);

    /// <summary>
    /// Pergunta o resultado de uma cobrança já enviada. Nulo se a cobrança não chegou na maquininha.
    /// <see cref="MaquininhaSemResposta"/>: não deu para perguntar ou a maquininha não sabe dizer se foi paga; o
    /// pedido continua esperando o operador conferir.
    /// </summary>
    /// <param name="valorCentavos">O valor da cobrança: a resposta só vale se for daquele valor.</param>
    Task<ResultadoCobranca?> ConsultarAsync(string id, long valorCentavos, CancellationToken cancelar);
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
        // Termina junto com esta cobrança (aprovada, recusada ou cancelada): o que a preparação deixou esperando
        // (ex.: o "aprova sozinho" do simulador) não pode decidir a cobrança seguinte.
        using var desta = CancellationTokenSource.CreateLinkedTokenSource(cancelar);
        try
        {
            await Preparar(cobranca, andamento, desta.Token);
            return await decisao.Task;
        }
        finally
        {
            desta.Cancel();
            _decisao = null;
        }
    }

    public virtual Task<ResultadoCobranca?> ConsultarAsync(string id, long valorCentavos, CancellationToken cancelar) =>
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
