namespace BCFichas.Core.Pagamento;

/// <summary>
/// Maquininha de mentira para testar o sistema sem cartão. Aprova sozinha depois de alguns segundos
/// ou espera o operador tocar em "Aprovar" / "Recusar" na tela.
/// </summary>
public sealed class MaquininhaSimulada : IMaquininha
{
    private TaskCompletionSource<ResultadoCobranca>? _decisao;
    private int _autorizacao = 100000;

    public MaquininhaSimulada(int aprovarEmSegundos = 0) => AprovarEmSegundos = aprovarEmSegundos;

    public string Nome => "Simulador";

    /// <summary>0 = espera o operador decidir.</summary>
    public int AprovarEmSegundos { get; set; }

    public bool AguardandoDecisao => _decisao is { Task.IsCompleted: false };

    public async Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento,
        CancellationToken cancelar)
    {
        andamento?.Report("Enviando para a maquininha...");
        await Task.Delay(300, cancelar);

        var decisao = new TaskCompletionSource<ResultadoCobranca>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decisao = decisao;

        andamento?.Report(cobranca.Forma == FormaPagamento.Pix
            ? $"QR Code PIX de {Dinheiro.Formatar(cobranca.ValorCentavos)} na tela da maquininha"
            : $"Insira ou aproxime o cartão ({Nomes.De(cobranca.Forma)})");

        using var registro = cancelar.Register(() => decisao.TrySetCanceled(cancelar));
        if (AprovarEmSegundos > 0)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(AprovarEmSegundos), cancelar)
                .ContinueWith(t => { if (!t.IsCanceled) Aprovar(); }, TaskScheduler.Default);
        }

        try
        {
            return await decisao.Task;
        }
        finally
        {
            _decisao = null;
        }
    }

    public Task<ResultadoCobranca?> ConsultarAsync(string id, CancellationToken cancelar) =>
        Task.FromResult<ResultadoCobranca?>(null);

    public void Aprovar() =>
        _decisao?.TrySetResult(new ResultadoCobranca(true, "Pagamento aprovado",
            "SIM" + Interlocked.Increment(ref _autorizacao)));

    public void Recusar(string motivo = "Pagamento recusado pelo simulador") =>
        _decisao?.TrySetResult(new ResultadoCobranca(false, motivo));
}
