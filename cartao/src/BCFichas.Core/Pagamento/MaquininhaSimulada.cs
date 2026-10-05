namespace BCFichas.Core.Pagamento;

/// <summary>
/// Maquininha de mentira para testar o sistema sem cartão. Aprova sozinha depois de alguns segundos
/// ou espera o operador tocar em "Aprovar" / "Recusar" na tela.
/// </summary>
public sealed class MaquininhaSimulada : MaquininhaManual
{
    private int _autorizacao = 100000;

    public MaquininhaSimulada(int aprovarEmSegundos = 0) => AprovarEmSegundos = aprovarEmSegundos;

    public override string Nome => "Simulador";
    public override string TextoAprovar => "Simular aprovado";
    public override string TextoRecusar => "Simular recusado";

    /// <summary>0 = espera o operador decidir.</summary>
    public int AprovarEmSegundos { get; set; }

    protected override async Task Preparar(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar)
    {
        andamento?.Report("Enviando para a maquininha...");
        await Task.Delay(300, cancelar);

        andamento?.Report(cobranca.Forma == FormaPagamento.Pix
            ? $"QR Code PIX de {Dinheiro.Formatar(cobranca.ValorCentavos)} na tela da maquininha"
            : $"Insira ou aproxime o cartão ({Nomes.De(cobranca.Forma)})");

        if (AprovarEmSegundos > 0)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(AprovarEmSegundos), cancelar)
                .ContinueWith(t => { if (!t.IsCanceled) Aprovar(); }, TaskScheduler.Default);
        }
    }

    public void Aprovar() => Aprovar("SIM" + Interlocked.Increment(ref _autorizacao));

    public void Recusar() => Recusar("Pagamento recusado pelo simulador");
}
