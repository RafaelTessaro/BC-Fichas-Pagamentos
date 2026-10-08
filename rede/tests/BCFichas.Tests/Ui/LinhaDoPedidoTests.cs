using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Tocar em cima de um item do pedido tira 1, igual ao − (com 1, o item sai do pedido). O − e o +
/// continuam valendo cada um no seu (o toque neles não tira mais 1 pela linha). Toques de verdade, como o dedo.
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

        // No número da quantidade (entre o − e o +) é a linha: tira 1
        await Task.Delay(400);
        TocarEm(t, Centro(t, Texto(Linha(t, "PASTEL"), "3")));
        Assert.Equal(2, Quantidade(t, "PASTEL"));
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
}
