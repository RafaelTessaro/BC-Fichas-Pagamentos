using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Cadastro do combo na tela de produtos, venda do combo e devolução de ficha do combo.</summary>
public class TelasDeComboTests
{
    [AvaloniaFact]
    public async Task Cadastra_combo_heineken_vende_e_devolve_uma_ficha()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        t.AbrirCaixa(10000);
        var heineken = t.Sistema.Catalogo.Produtos().First(p => p.Nome == "HEINEKEN");
        heineken.PrecoCentavos = 650;
        t.Sistema.Catalogo.SalvarProduto(heineken, 12);

        // Grade 4 x 3 cheia: aumenta para caber o combo
        var config = t.Sistema.Config.Atual.Clonar();
        config.Linhas = 4;
        t.Sistema.Config.Salvar(config);
        TelaDeTeste.Atualizar();

        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Nome = "Combo Heineken";
        tela.Preco = "30,00";
        tela.Cor = "#2B8A3E";
        tela.EhCombo = true;
        Assert.Equal("Escolha os produtos que saem nas fichas e toque em Adicionar.", tela.ResumoCombo);
        tela.ParaAdicionar = tela.ParaOCombo.Single(p => p.Nome == "HEINEKEN");
        tela.AdicionarAoComboCommand.Execute(null);
        var linha = Assert.Single(tela.Componentes);
        Assert.Equal("6,50", linha.Valor);
        for (var i = 0; i < 4; i++) linha.MaisCommand.Execute(null);
        Assert.Equal("5 ficha(s) • as fichas somam R$ 32,50 • preço do combo R$ 30,00 (R$ 2,50 de desconto)",
            tela.ResumoCombo);
        TelaDeTeste.Atualizar();
        t.Achar<Border>(b => b.Name == "BlocoCombo").BringIntoView();
        TelaDeTeste.Atualizar();
        t.Foto("40-produtos-combo");
        tela.SalvarCommand.Execute(null);
        Assert.Equal("COMBO HEINEKEN salvo.", t.Principal.Aviso);

        var combo = t.Sistema.Catalogo.Produtos().Single(p => p.Nome == "COMBO HEINEKEN");
        Assert.Equal(5, Assert.Single(combo.Componentes).Quantidade);
        Assert.Equal(650, combo.Componentes[0].ValorCentavos);

        // Na venda o botão diz que é combo e quantas fichas saem
        t.Principal.IrParaVenda();
        TelaDeTeste.Atualizar();
        var botao = t.Venda.Botoes.Single(b => b.Nome == "COMBO HEINEKEN");
        Assert.Equal("COMBO • 5 FICHAS", botao.Detalhe);
        t.Tocar("COMBO HEINEKEN");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        Assert.Equal("1 item(ns) • 5 ficha(s)", pagamento.ResumoItens);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.ValorExatoCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        Assert.Equal(5, t.EsperarImpressoes(5).Length);
        pagamento.FecharCommand.Execute(null);

        var resumo = t.Sistema.Caixa.Resumo(t.Principal.Sessao!.Id);
        Assert.Equal("COMBO HEINEKEN", Assert.Single(resumo.Produtos).Nome);

        // Devolução: o cliente devolve 1 das 5 Heineken do combo
        var pedido = t.Sistema.Vendas.Pedidos(t.Principal.Sessao.Id).Single();
        var devolucao = new DevolucaoViewModel(t.Principal);
        t.Principal.Abrir(devolucao);
        devolucao.Busca = pedido.Numero.ToString();
        devolucao.BuscarCommand.Execute(null);
        var item = Assert.Single(devolucao.Itens);
        Assert.Equal("HEINEKEN", item.Nome);
        Assert.Equal("Ficha do COMBO HEINEKEN", item.Combo);
        item.MaisCommand.Execute(null);
        Assert.Equal("R$ 6,00", devolucao.TotalTexto);
        t.Principal.Aviso = null;
        TelaDeTeste.Atualizar();
        t.Foto("41-devolucao-combo");
        await devolucao.RegistrarCommand.ExecuteAsync(null);
        Assert.Equal(600, t.Sistema.Caixa.Resumo(t.Principal.Sessao.Id).TotalDevolvido);
    }

    [AvaloniaFact]
    public void Produto_fora_da_tela_de_venda_nao_precisa_de_posicao()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Nome = "Vale R$ 2,00";
        tela.Preco = "2,00";
        tela.Ativo = false;
        // A aba ITENS está cheia (12 botões), mas o vale não vai para a grade
        Assert.Null(tela.Posicao);
        tela.SalvarCommand.Execute(null);
        Assert.Equal("VALE R$ 2,00 salvo.", t.Principal.Aviso);
        var vale = t.Sistema.Catalogo.Produtos().Single(p => p.Nome == "VALE R$ 2,00");
        Assert.False(vale.Ativo);
        Assert.Contains(tela.Lista, p => p.Nome == "VALE R$ 2,00" && p.Info.Contains("fora da tela de venda"));
    }
}
