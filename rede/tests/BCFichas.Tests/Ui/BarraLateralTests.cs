using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Barra lateral da venda (3.11.5): estreita, com o logo e o caixa em cima, o Menu verde, as abas (no máximo 4) no
/// meio e Sangria (laranja) e Fechar caixa (vermelho) embaixo, um sobre o outro. Monta a tela, tira as fotos e confere que as abas ficam inteiras (sem rolar) e com
/// 48 de altura ou mais, inclusive no caso mais apertado (1024x600, com operador e 4 abas).
/// </summary>
public class BarraLateralTests(ITestOutputHelper saida)
{
    /// <summary>A 4ª aba (o máximo): nome comprido, para conferir a quebra em duas linhas.</summary>
    private const string QuartaAba = "COMBOS DA FESTA";

    private const string Operador = "MARIA APARECIDA DOS SANTOS";

    private static void MontarPedido(TelaDeTeste t)
    {
        t.Tocar("PASTEL");
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        t.Venda.SelecionarAbaCommand.Execute(t.Venda.Abas[1]);
        TelaDeTeste.Atualizar();
        t.Tocar("REFRIGERANTE");
    }

    private static void CriarQuartaAba(TelaDeTeste t) =>
        t.Sistema.Catalogo.SalvarAba(new BCFichas.Core.Aba { Nome = QuartaAba });

    /// <summary>Deixa só 2 abas (tira a última, com os produtos dela).</summary>
    private static void TirarTerceiraAba(TelaDeTeste t)
    {
        var catalogo = t.Sistema.Catalogo;
        var ultima = catalogo.Abas()[^1];
        foreach (var produto in catalogo.ProdutosDaAba(ultima.Id)) catalogo.ExcluirProduto(produto.Id);
        catalogo.ExcluirAba(ultima.Id);
    }

    /// <summary>
    /// As abas cabem todas (a lista não rola, nada esmaece) e cada uma tem pelo menos 48 de altura (o toque).
    /// Escreve as medidas na saída do teste.
    /// </summary>
    private void ConferirAbas(TelaDeTeste t, string nome, int quantas)
    {
        TelaDeTeste.Atualizar();
        var miolo = t.Janela.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("miolo-barra"));
        var lista = t.Janela.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ListaAbas");
        var abas = t.Janela.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("aba-cartao") && b.IsEffectivelyVisible).ToList();
        var alturas = string.Join(", ", abas.Select(a => a.Bounds.Height.ToString("0.#")));
        saida.WriteLine($"{nome}: {abas.Count} abas de {alturas}; janela da lista {lista.Viewport.Height:0.#}, " +
                        $"conteúdo {lista.Extent.Height:0.#}");
        Assert.Equal(quantas, abas.Count);
        Assert.DoesNotContain("rola", miolo.Classes);
        Assert.True(lista.Extent.Height <= lista.Viewport.Height + 0.5, $"{nome}: a lista das abas rola");
        Assert.All(abas, a => Assert.True(a.Bounds.Height >= 48, $"{nome}: aba com {a.Bounds.Height:0.#} de altura"));
    }

    [AvaloniaFact]
    public void Foto_1280x800_com_pedido()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        Assert.Equal(3, t.Venda.Abas.Count);
        MontarPedido(t);
        Assert.Equal("R$ 38,00", t.Venda.Total);
        ConferirAbas(t, "1280x800-pedido", 3);
        t.Foto("barra-1280x800-pedido");
    }

    [AvaloniaFact]
    public void Foto_1024x600_com_pedido()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        Assert.Equal(3, t.Venda.Abas.Count);
        MontarPedido(t);
        ConferirAbas(t, "1024x600-pedido", 3);
        t.Foto("barra-1024x600-pedido");
    }

    [AvaloniaFact]
    public void Foto_1280x800_com_4_abas()
    {
        using var t = new TelaDeTeste();
        CriarQuartaAba(t);
        t.AbrirCaixa();
        Assert.Equal(4, t.Venda.Abas.Count);
        MontarPedido(t);
        ConferirAbas(t, "1280x800-4-abas", 4);
        t.Foto("barra-1280x800-4-abas");
    }

    [AvaloniaFact]
    public void Foto_1024x600_com_4_abas()
    {
        using var t = new TelaDeTeste(1024, 600);
        CriarQuartaAba(t);
        t.AbrirCaixa();
        Assert.Equal(4, t.Venda.Abas.Count);
        MontarPedido(t);
        ConferirAbas(t, "1024x600-4-abas", 4);
        t.Foto("barra-1024x600-4-abas");
    }

    // Fotos extras, só para conferência (não vão para a documentação)
    private static async Task EntrarNoModoTeste(TelaDeTeste t)
    {
        var entrar = t.Principal.EntrarNoModoTeste();
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await entrar;
        await TelaDeTeste.Esperar(() => t.Principal.ModoTeste && t.Principal.Pagina is VendaViewModel);
        t.Principal.Aviso = null;
    }

    [AvaloniaFact]
    public async Task Foto_1024x600_modo_teste_com_operador()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Principal.Operador = Operador;
        await EntrarNoModoTeste(t);
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        ConferirAbas(t, "x-1024x600-modo-teste", 3);
        t.Foto("barra-x-1024x600-modo-teste");
    }

    [AvaloniaFact]
    public async Task Foto_1280x800_modo_teste_com_operador()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Principal.Operador = Operador;
        await EntrarNoModoTeste(t);
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        ConferirAbas(t, "x-1280x800-modo-teste", 3);
        t.Foto("barra-x-1280x800-modo-teste");
    }

    [AvaloniaFact]
    public void Foto_1024x600_com_operador()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Principal.Operador = Operador;
        MontarPedido(t);
        ConferirAbas(t, "x-1024x600-operador", 3);
        t.Foto("barra-x-1024x600-operador");
    }

    // Os casos mais apertados: operador (uma linha a mais em cima) com 4 abas, e o modo teste com 4 abas

    [AvaloniaFact]
    public void Foto_1024x600_com_operador_e_4_abas()
    {
        using var t = new TelaDeTeste(1024, 600);
        CriarQuartaAba(t);
        t.AbrirCaixa();
        t.Principal.Operador = Operador;
        MontarPedido(t);
        ConferirAbas(t, "x-1024x600-operador-4-abas", 4);
        t.Foto("barra-x-1024x600-operador-4-abas");
    }

    [AvaloniaFact]
    public void Foto_1280x800_com_operador_e_4_abas()
    {
        using var t = new TelaDeTeste();
        CriarQuartaAba(t);
        t.AbrirCaixa();
        t.Principal.Operador = Operador;
        MontarPedido(t);
        ConferirAbas(t, "x-1280x800-operador-4-abas", 4);
        t.Foto("barra-x-1280x800-operador-4-abas");
    }

    [AvaloniaFact]
    public async Task Foto_1024x600_modo_teste_com_4_abas()
    {
        using var t = new TelaDeTeste(1024, 600);
        CriarQuartaAba(t);
        t.AbrirCaixa();
        await EntrarNoModoTeste(t);
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        ConferirAbas(t, "x-1024x600-modo-teste-4-abas", 4);
        t.Foto("barra-x-1024x600-modo-teste-4-abas");
    }

    [AvaloniaFact]
    public async Task Foto_1280x800_modo_teste_com_4_abas()
    {
        using var t = new TelaDeTeste();
        CriarQuartaAba(t);
        t.AbrirCaixa();
        await EntrarNoModoTeste(t);
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        ConferirAbas(t, "x-1280x800-modo-teste-4-abas", 4);
        t.Foto("barra-x-1280x800-modo-teste-4-abas");
    }

    // Só 2 abas: o grupo das abas não pode deixar um buraco esquisito

    [AvaloniaFact]
    public void Foto_1024x600_com_2_abas()
    {
        using var t = new TelaDeTeste(1024, 600);
        TirarTerceiraAba(t);
        t.AbrirCaixa();
        Assert.Equal(2, t.Venda.Abas.Count);
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        ConferirAbas(t, "x-1024x600-2-abas", 2);
        t.Foto("barra-x-1024x600-2-abas");
    }

    [AvaloniaFact]
    public void Foto_1280x800_com_2_abas()
    {
        using var t = new TelaDeTeste();
        TirarTerceiraAba(t);
        t.AbrirCaixa();
        Assert.Equal(2, t.Venda.Abas.Count);
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        ConferirAbas(t, "x-1280x800-2-abas", 2);
        t.Foto("barra-x-1280x800-2-abas");
    }
}
