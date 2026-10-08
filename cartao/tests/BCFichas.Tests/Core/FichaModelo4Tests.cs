using BCFichas.Core;
using BCFichas.Core.Impressao;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Modelo 4 com pedido de 5 números e 10 fichas ou mais: "PED: 12345 (10/12)", a data e o caixa na mesma linha não
/// podem ficar um por cima do outro (nem no papel de 58 mm). Fotos em saida/fichas para conferir.
/// </summary>
public sealed class FichaModelo4Tests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly string Pasta =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "saida", "fichas"));

    [Theory]
    [InlineData(80)]
    [InlineData(58)]
    public void Pedido_grande_nao_encosta_na_data(int milimetros)
    {
        var config = _t.Sistema.Config.Atual.Clonar();
        config.Modelo = ModeloFicha.Classico4;
        config.LarguraPapelMm = milimetros;
        var ficha = new Ficha
        {
            NomeEvento = "FESTA DE SÃO JOÃO", Produto = "CERVEJA", PrecoCentavos = 800, NumeroPedido = 12345, Caixa = 12,
            Data = new DateTime(2026, 10, 8, 20, 46, 12), Sequencia = 10, TotalFichas = 12, Forma = FormaPagamento.Dinheiro,
        };
        using var desenho = RenderizadorFicha.Renderizar(ficha, config, null);
        Directory.CreateDirectory(Pasta);
        using var mono = ImagemUtil.Monocromatico(desenho);
        ImagemUtil.SalvarPng(mono, Path.Combine(Pasta, $"ficha-modelo4-pedido-grande-{milimetros}mm.png"));
        Assert.True(desenho.Height > 100);
    }
}
