using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Versão 3.10: cada aba com a sua grade, sem espaço vazio na tela de venda; Limpar no dinheiro.</summary>
public class TelasDaVersao310Tests
{
    private const int AbaBotoes = 2;

    // ---------- A conta da grade ----------

    [Fact]
    public void Grade_fixa_4x2_com_7_produtos_fica_4_mais_3_sem_espaco_vazio()
    {
        var arranjo = GradeDeBotoes.Calcular(7, 760, 740, colunas: 4, linhas: 2);
        Assert.Equal([4, 3], arranjo.PorLinha);
        Assert.Equal(7, arranjo.Botoes.Count);
        // Linha de cima: 4 botões de 190; a de baixo estica: 3 de 253,3
        Assert.All(arranjo.Botoes.Take(4), b => Assert.Equal(190, b.Width, 1));
        Assert.All(arranjo.Botoes.Skip(4), b => Assert.Equal(760 / 3.0, b.Width, 1));
        SemEspacoVazio(arranjo, 760, 740);
    }

    [Fact]
    public void Grade_fixa_com_poucos_produtos_as_linhas_que_sobram_somem()
    {
        // 4 × 3 com 5 produtos: 2 linhas (4 + 1), os botões ficam mais altos
        var arranjo = GradeDeBotoes.Calcular(5, 760, 740, colunas: 4, linhas: 3);
        Assert.Equal([4, 1], arranjo.PorLinha);
        Assert.Equal(370, arranjo.Botoes[0].Height, 1);
        SemEspacoVazio(arranjo, 760, 740);
        // Mais produtos que a grade: mostra só os que cabem
        Assert.Equal(8, GradeDeBotoes.Calcular(11, 760, 740, colunas: 4, linhas: 2).Botoes.Count);
    }

    [Theory]
    [InlineData(1, 1, new[] { 1 })]
    [InlineData(7, 4, new[] { 4, 3 })]       // o exemplo do dono, numa área larga
    [InlineData(10, 4, new[] { 4, 3, 3 })]   // linhas equilibradas, não 4 + 4 + 2
    [InlineData(12, 4, new[] { 4, 4, 4 })]
    public void Grade_automatica_escolhe_a_arrumacao_de_botoes_maiores(int produtos, int colunas, int[] porLinha)
    {
        var arranjo = GradeDeBotoes.Calcular(produtos, 1000, 600);
        Assert.Equal(colunas, arranjo.Colunas);
        Assert.Equal(porLinha, arranjo.PorLinha);
        SemEspacoVazio(arranjo, 1000, 600);
    }

    [Fact]
    public void Grade_automatica_nunca_deixa_espaco_vazio_e_nao_achata_os_botoes()
    {
        for (var n = 1; n <= Aba.MaximoAutomatico; n++)
        {
            var arranjo = GradeDeBotoes.Calcular(n, 760, 740);
            Assert.Equal(n, arranjo.Botoes.Count);
            SemEspacoVazio(arranjo, 760, 740);
            // As linhas diferem no máximo em um botão
            Assert.True(arranjo.PorLinha.Max() - arranjo.PorLinha.Min() <= 1, $"{n}: {string.Join("+", arranjo.PorLinha)}");
            Assert.True(arranjo.Colunas <= GradeDeBotoes.MaximoColunasAutomatico);
        }
        // Com bastante produto, os botões continuam fáceis de tocar (no mínimo uns 2 cm no tablet de 9")
        Assert.All(GradeDeBotoes.Calcular(Aba.MaximoAutomatico, 760, 740).Botoes, b => Assert.True(b.Width >= 120 && b.Height >= 120));
    }

    /// <summary>Os botões cobrem a área inteira, sem buraco e sem um em cima do outro.</summary>
    private static void SemEspacoVazio(ArranjoDaGrade arranjo, double largura, double altura)
    {
        Assert.Equal(largura * altura, arranjo.Botoes.Sum(b => b.Width * b.Height), 0);
        Assert.All(arranjo.Botoes, b => Assert.True(b.X >= -0.01 && b.Right <= largura + 0.01 && b.Y >= -0.01 &&
                                                     b.Bottom <= altura + 0.01));
    }

    // ---------- Tela de venda e configurações ----------

    private static ConfiguracaoViewModel AbrirBotoesEAbas(TelaDeTeste t)
    {
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = AbaBotoes;
        TelaDeTeste.Atualizar();
        return tela;
    }

    [AvaloniaFact]
    public void Cada_aba_tem_a_sua_grade_e_a_tela_de_venda_nao_deixa_espaco_vazio()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        var tela = AbrirBotoesEAbas(t);
        var itens = Assert.Single(tela.Abas);
        Assert.Equal("Automática", itens.GradeTexto);
        Assert.Equal("12 produtos", itens.ProdutosTexto);

        // Escolhe 4 × 2 para a aba: na prévia aparecem os produtos dela, e avisa que 4 ficam de fora
        tela.GradeDaAbaCommand.Execute(itens);
        var grade = Assert.IsType<GradeAbaViewModel>(t.Principal.Dialogo);
        Assert.Equal("Botões da aba ITENS", grade.Titulo);
        Assert.True(grade.Automatica);
        Assert.Equal(12, grade.Previa.Count);
        grade.EscolherFixaCommand.Execute(null);
        grade.Colunas = 4;
        grade.Linhas = 2;
        Assert.Equal(8, grade.Previa.Count);
        Assert.Contains("só cabem 8: 4 ficariam fora da tela", grade.Aviso);
        grade.Linhas = 3;
        Assert.Equal("", grade.Aviso);
        TelaDeTeste.Atualizar();
        t.Foto("54-config-grade-da-aba");
        grade.AplicarCommand.Execute(null);
        Assert.Null(t.Principal.Dialogo);
        Assert.Equal("4 × 3", itens.GradeTexto);
        Assert.True(tela.TemAlteracoes); // vale ao tocar em Salvar, como o resto das abas
        TelaDeTeste.Atualizar();
        t.Foto("23-config-botoes-abas");

        tela.SalvarCommand.Execute(null);
        var salva = t.Sistema.Catalogo.Abas().Single();
        Assert.Equal((4, 3), (salva.Colunas, salva.Linhas));

        // Tira 5 produtos da tela (fica com 7): a grade 4 × 3 vira 4 + 3, sem espaço vazio
        foreach (var p in t.Sistema.Catalogo.Produtos().Skip(7))
        {
            p.Ativo = false;
            t.Sistema.Catalogo.SalvarProduto(p, 12);
        }
        t.Principal.Iniciar();
        t.AbrirCaixa();
        TelaDeTeste.Atualizar();
        Assert.Equal(7, t.Venda.Botoes.Count);
        var botoes = t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("produto") && b.IsEffectivelyVisible).ToList();
        Assert.Equal(7, botoes.Count);
        var larguras = botoes.Select(b => Math.Round(b.Bounds.Width)).ToList();
        Assert.True(larguras.Skip(4).Min() > larguras.Take(4).Max()); // a linha de baixo estica
        t.Foto("53-venda-sem-espaco-vazio");
    }

    [AvaloniaFact]
    public void Aba_nova_comeca_automatica_e_sem_produtos_mostra_aviso_na_venda()
    {
        using var t = new TelaDeTeste();
        var tela = AbrirBotoesEAbas(t);
        tela.NovaAbaCommand.Execute(null);
        var nova = tela.Abas.Last();
        nova.Nome = "Sobremesas";
        Assert.True(nova.Automatica);
        tela.GradeDaAbaCommand.Execute(nova);
        var grade = Assert.IsType<GradeAbaViewModel>(t.Principal.Dialogo);
        Assert.True(grade.SemProdutos);
        grade.CancelarCommand.Execute(null);
        tela.SalvarCommand.Execute(null);

        t.Principal.Iniciar();
        t.AbrirCaixa();
        t.Venda.SelecionarAbaCommand.Execute(t.Venda.Abas.Single(a => a.Nome == "SOBREMESAS"));
        TelaDeTeste.Atualizar();
        Assert.True(t.Venda.SemProdutos);
        Assert.Empty(t.Venda.Botoes);
        t.Foto("55-venda-aba-sem-produtos");
    }

    [AvaloniaFact]
    public void Produtos_a_posicao_vai_ate_o_tamanho_da_grade_da_aba()
    {
        using var t = new TelaDeTeste();
        var comidas = t.Sistema.Catalogo.Abas().First(a => a.Nome == "COMIDAS");
        comidas.Colunas = 3;
        comidas.Linhas = 2;
        t.Sistema.Catalogo.SalvarAba(comidas);
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.NovoCommand.Execute(null);
        tela.Aba = tela.Abas.First(a => a.Nome == "COMIDAS");
        Assert.All(tela.Posicoes, p => Assert.InRange(p, 1, 6));
        tela.Aba = tela.Abas.First(a => a.Nome == "BEBIDAS"); // automática: até 30
        Assert.Contains(Aba.MaximoAutomatico, tela.Posicoes);
    }

    [AvaloniaFact]
    public void Dinheiro_tem_botao_limpar()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.TeclaCommand.Execute("9");
        pagamento.Recebido.TeclaCommand.Execute("9");
        TelaDeTeste.Atualizar();
        var limpar = t.Achar<Avalonia.Controls.Button>(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Limpar o valor");
        limpar.Command!.Execute(limpar.CommandParameter);
        Assert.Equal("R$ 0,00", pagamento.Recebido.Texto);
    }
}
