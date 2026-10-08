using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Tocar em cima de um item do pedido tira 1, igual ao − (com 1, o item sai do pedido). O − e o +
/// continuam valendo cada um no seu, e logo em volta deles (errou por pouco) o toque não faz nada. Toques de
/// verdade, como o dedo.
/// </summary>
public class LinhaDoPedidoTests
{
    private static void TocarEm(TelaDeTeste t, Point ponto)
    {
        t.Janela.MouseDown(ponto, MouseButton.Left);
        t.Janela.MouseUp(ponto, MouseButton.Left);
        TelaDeTeste.Atualizar();
    }

    private static Point Centro(TelaDeTeste t, Control alvo) =>
        alvo.TranslatePoint(new Point(alvo.Bounds.Width / 2, alvo.Bounds.Height / 2), t.Janela)!.Value;

    private static Rect Retangulo(TelaDeTeste t, Control alvo) =>
        new(alvo.TranslatePoint(new Point(0, 0), t.Janela)!.Value, alvo.Bounds.Size);

    private static Button Linha(TelaDeTeste t, string produto) =>
        t.Janela.GetVisualDescendants().OfType<Button>().First(b =>
            b.IsEffectivelyVisible && b.Classes.Contains("linha-pedido") && b.DataContext is LinhaItem l && l.Nome == produto);

    private static TextBlock Texto(Button linha, string texto) =>
        linha.GetVisualDescendants().OfType<TextBlock>().First(x => x.IsEffectivelyVisible && x.Text == texto);

    /// <summary>O − ou o + pequeno de dentro da linha.</summary>
    private static Button Botao(Button linha, System.Windows.Input.ICommand comando) =>
        linha.GetVisualDescendants().OfType<Button>().First(b => b.Command == comando);

    private static int Quantidade(TelaDeTeste t, string produto) =>
        t.Venda.Linhas.FirstOrDefault(l => l.Nome == produto)?.Quantidade ?? 0;

    [AvaloniaFact]
    public async Task Tocar_no_item_tira_um_e_com_um_o_item_sai_do_pedido()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("PASTEL");
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        Assert.Equal("R$ 42,00", t.Venda.Total);

        // No nome do produto: tira 1
        TocarEm(t, Centro(t, Texto(Linha(t, "PASTEL"), "PASTEL")));
        Assert.Equal(2, Quantidade(t, "PASTEL"));
        Assert.Equal("R$ 32,00", t.Venda.Total);

        // No preço da unidade, logo em seguida (a mesma linha continua embaixo do dedo): vale de novo
        TocarEm(t, Centro(t, Texto(Linha(t, "PASTEL"), "R$ 10,00")));
        Assert.Equal(1, Quantidade(t, "PASTEL"));

        // Com 1, o item sai do pedido
        var ponto = Centro(t, Texto(Linha(t, "PASTEL"), "PASTEL"));
        TocarEm(t, ponto);
        Assert.Equal(0, Quantidade(t, "PASTEL"));
        Assert.Equal("ESPETINHO", Assert.Single(t.Venda.Linhas).Nome);
        Assert.Equal("R$ 12,00", t.Venda.Total);

        // A linha do ESPETINHO subiu para baixo do dedo: um toque duplo não tira o espetinho junto
        TocarEm(t, ponto);
        Assert.Equal(1, Quantidade(t, "ESPETINHO"));

        // Passado o instante do toque duplo, tocar nele vale: o pedido fica vazio
        await Task.Delay(400);
        TocarEm(t, ponto);
        Assert.True(t.Venda.Vazio);
        Assert.Equal("R$ 0,00", t.Venda.Total);
    }

    [AvaloniaFact]
    public async Task O_menos_e_o_mais_continuam_cada_um_no_seu()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("PASTEL");

        // O + soma 1 (o toque nele não chega na linha, que tiraria 1)
        var mais = Centro(t, Botao(Linha(t, "PASTEL"), t.Venda.MaisCommand));
        TocarEm(t, mais);
        Assert.Equal(3, Quantidade(t, "PASTEL"));
        TocarEm(t, mais);
        Assert.Equal(4, Quantidade(t, "PASTEL"));

        // O − tira só 1, não 2
        await Task.Delay(400);
        TocarEm(t, Centro(t, Botao(Linha(t, "PASTEL"), t.Venda.MenosCommand)));
        Assert.Equal(3, Quantidade(t, "PASTEL"));
        Assert.Equal("R$ 30,00", t.Venda.Total);

        // No número da quantidade e logo em volta do − e do + (errou por pouco) não faz nada: errar o + não pode
        // tirar 1 pela linha
        await Task.Delay(400);
        var linha = Linha(t, "PASTEL");
        var menos = Retangulo(t, Botao(linha, t.Venda.MenosCommand));
        var maisR = Retangulo(t, Botao(linha, t.Venda.MaisCommand));
        Point[] pertinho =
        [
            Centro(t, Texto(linha, "3")),
            new(maisR.Right + 3, maisR.Center.Y), // à direita do +
            new(maisR.Center.X, maisR.Bottom + 5), // embaixo do +
            new(maisR.Center.X, maisR.Y - 6), // em cima do +
            new(menos.X - 5, menos.Center.Y), // à esquerda do −
            new(menos.Center.X, menos.Bottom + 5), // embaixo do −
        ];
        foreach (var ponto in pertinho)
        {
            TocarEm(t, ponto);
            Assert.Equal(3, Quantidade(t, "PASTEL"));
            Assert.False(linha.IsPressed);
        }

        // Já no nome, no preço ou no valor da linha, tira 1
        TocarEm(t, Centro(t, Texto(linha, "R$ 30,00")));
        Assert.Equal(2, Quantidade(t, "PASTEL"));
    }

    /// <summary>
    /// A lista do pedido não segue rolando sozinha (um toque para frear cairia numa linha e tiraria 1) e aguenta um
    /// toque um pouco tremido sem virar rolagem (16 em vez dos 5 do Windows).
    /// </summary>
    [AvaloniaFact]
    public void A_lista_do_pedido_nao_embala_e_aguenta_um_toque_tremido()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        var lista = t.Janela.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ListaPedido");
        Assert.False(lista.IsScrollInertiaEnabled);
        var miolo = lista.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var rolagem = Assert.Single(miolo.GestureRecognizers.OfType<ScrollGestureRecognizer>());
        Assert.Equal(VendaView.FolgaDoToqueNoPedido, rolagem.ScrollStartDistance);
    }

    [AvaloniaFact]
    public void A_linha_apertada_fica_vermelho_clara_e_parece_a_de_sempre_solta()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        t.Tocar("CALDO");
        var linha = Linha(t, "ESPETINHO");
        // Ocupa a largura toda do pedido, com o fio embaixo, sem fundo
        var lista = linha.FindAncestorOfType<ItemsControl>()!;
        Assert.Equal(lista.Bounds.Width, linha.Bounds.Width, 1);
        Assert.Equal(new Thickness(0, 0, 0, 1), linha.BorderThickness);
        Assert.Equal(Avalonia.Media.Colors.Transparent, ((Avalonia.Media.ISolidColorBrush)linha.Background!).Color);
        t.Foto("pedido-linha-solta-1024x600");

        var ponto = Centro(t, Texto(linha, "ESPETINHO"));
        t.Janela.MouseDown(ponto, MouseButton.Left);
        TelaDeTeste.Atualizar();
        Assert.True(linha.IsPressed);
        t.Foto("pedido-linha-apertada-1024x600");
        t.Janela.MouseUp(ponto, MouseButton.Left);
        TelaDeTeste.Atualizar();
        Assert.False(linha.IsPressed);
        Assert.Equal(0, Quantidade(t, "ESPETINHO"));
    }

    /// <summary>
    /// O troco da venda anterior some depois de 5 s: com itens no pedido, as linhas não podem subir (um toque bem
    /// nessa hora tiraria o item de baixo). Com o pedido vazio, o lugar do troco some de vez.
    /// </summary>
    [AvaloniaFact]
    public async Task Troco_sumindo_nao_mexe_nas_linhas_do_pedido()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("2000");
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        Assert.True(t.Venda.MostrarTroco);

        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        t.Tocar("CALDO");
        var pastel = Linha(t, "PASTEL");
        var mira = Centro(t, Texto(pastel, "PASTEL"));
        var antes = pastel.TranslatePoint(new Point(0, 0), t.Janela)!.Value.Y;

        // Os 5 s acabam enquanto o dedo vai até o PASTEL (o mesmo que o tique do relógio do troco faz)
        t.Venda.EsconderTroco();
        TelaDeTeste.Atualizar();
        Assert.False(t.Venda.MostrarTroco);
        Assert.Equal(antes, Linha(t, "PASTEL").TranslatePoint(new Point(0, 0), t.Janela)!.Value.Y);
        TocarEm(t, mira);
        Assert.Equal(0, Quantidade(t, "PASTEL"));
        Assert.Equal(1, Quantidade(t, "ESPETINHO"));

        // Pedido vazio: o lugar do troco some (as linhas da próxima venda começam lá em cima)
        t.Venda.LimparPedido();
        TelaDeTeste.Atualizar();
        Assert.False(t.Venda.LugarDoTroco);
    }
}
