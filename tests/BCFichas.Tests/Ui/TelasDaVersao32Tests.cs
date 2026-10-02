using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Barra lateral, pedido com nome comprido, devolução, modo teste, sangria e configurações novas.</summary>
public class TelasDaVersao32Tests
{
    private static Pedido Vender(TelaDeTeste t, FormaPagamento forma, params (string Nome, int Qtd)[] itens)
    {
        var produtos = t.Sistema.Catalogo.Produtos().ToDictionary(p => p.Nome);
        var carrinho = new Carrinho();
        foreach (var (nome, qtd) in itens) carrinho.Adicionar(produtos[nome], qtd);
        var pedido = t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, forma,
            forma == FormaPagamento.Dinheiro ? 100000 : 0);
        if (forma != FormaPagamento.Dinheiro) pedido = t.Sistema.Vendas.ConfirmarPagamento(pedido.Id, null);
        t.Sistema.Vendas.RegistrarImpressao(pedido.Id);
        return pedido;
    }

    [AvaloniaFact]
    public void Barra_lateral_tem_marca_menu_caixa_e_relogio_e_o_nome_comprido_nao_esconde_o_valor()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        t.AbrirCaixa();
        t.Tocar("ENROLADINHO PORÇÃO");
        t.Tocar("BATATA PORÇÃO");
        t.Tocar("BATATA PORÇÃO");
        t.Tocar("PÃO DE MEL");
        t.Tocar("HEINEKEN");
        t.Foto("02-venda-barra-lateral");

        // Nada da barra de cima: menu, evento, caixa e relógio estão na lateral esquerda
        var menu = t.Achar<Button>(b => b.Command == t.Venda.AbrirMenuCommand);
        var posicaoMenu = menu.TranslatePoint(new Point(0, 0), t.Janela)!.Value;
        Assert.True(posicaoMenu.X < 40, "o menu fica na lateral esquerda");
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "BC-FICHAS" && b.IsEffectivelyVisible);
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "CAIXA 01" && b.IsEffectivelyVisible);

        // O total de cada linha do pedido fica inteiro dentro do cartão do pedido
        var cartao = t.Achar<Border>(b => b.Classes.Contains("cartao") && b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Pedido"));
        var direitaDoCartao = cartao.TranslatePoint(new Point(cartao.Bounds.Width, 0), t.Janela)!.Value.X;
        foreach (var total in new[] { "R$ 20,00", "R$ 40,00", "R$ 8,00", "R$ 10,00" })
        {
            var texto = t.Achar<TextBlock>(b => b.Text == total && b.FontSize >= 17);
            var direita = texto.TranslatePoint(new Point(texto.Bounds.Width, 0), t.Janela)!.Value.X;
            Assert.True(direita <= direitaDoCartao, $"{total} sai do cartão ({direita:0} > {direitaDoCartao:0})");
        }
    }

    [AvaloniaFact]
    public async Task Devolucao_em_dinheiro_acha_o_pedido_e_tira_da_gaveta()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa(10000);
        var pedido = Vender(t, FormaPagamento.Dinheiro, ("CACHORRO-QUENTE", 1), ("PASTEL", 1));
        Vender(t, FormaPagamento.Pix, ("CERVEJA", 2));

        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        menu.EscolherCommand.Execute(menu.Itens.Single(i => i.Titulo == "Devolver fichas"));
        var tela = Assert.IsType<DevolucaoViewModel>(t.Principal.Pagina);
        Assert.Equal("R$ 120,00", tela.DinheiroEmCaixa);
        t.Foto("33-devolucao-buscar");

        foreach (var digito in pedido.Numero.ToString()) tela.TeclaCommand.Execute(digito.ToString());
        tela.BuscarCommand.Execute(null);
        Assert.True(tela.TemPedido);
        Assert.True(tela.EmDinheiro);
        Assert.False(tela.PodeRegistrar);
        tela.Itens.Single(i => i.Nome == "PASTEL").MaisCommand.Execute(null);
        tela.Motivo = "acabou o pastel";
        Assert.Equal("R$ 10,00", tela.TotalTexto);
        Assert.StartsWith("Devolva R$ 10,00 em dinheiro", tela.Instrucao);
        Assert.True(tela.PodeRegistrar);
        TelaDeTeste.Atualizar();
        t.Foto("34-devolucao-dinheiro");

        await tela.RegistrarCommand.ExecuteAsync(null);
        Assert.False(tela.TemPedido);
        Assert.Single(tela.Feitas);
        Assert.Equal("R$ 110,00", tela.DinheiroEmCaixa);
        Assert.Single(t.EsperarImpressoes(1)); // comprovante da devolução
        var resumo = t.Sistema.Caixa.Resumo(t.Principal.Sessao!.Id);
        Assert.Equal(1000, resumo.Devolvido(FormaPagamento.Dinheiro));
        t.Foto("35-devolucao-feita");
    }

    [AvaloniaFact]
    public void Devolucao_no_pix_pede_para_confirmar_o_estorno_na_maquininha()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var pedido = Vender(t, FormaPagamento.Pix, ("CERVEJA", 2), ("PASTEL", 1));
        var tela = new DevolucaoViewModel(t.Principal);
        t.Principal.Abrir(tela);

        // Código de barras da ficha 2 (a 2ª cerveja): caixa 01, pedido, ficha 002
        tela.Busca = $"01{pedido.Numero:000000}002";
        tela.BuscarCommand.Execute(null);
        Assert.True(tela.NaMaquininha);
        Assert.Equal(1, tela.Itens.Single(i => i.Nome == "CERVEJA").Quantidade);
        Assert.Contains("estorno de R$ 8,00 no PIX", tela.Instrucao);
        Assert.False(tela.PodeRegistrar);
        tela.EstornoFeito = true;
        Assert.True(tela.PodeRegistrar);
        TelaDeTeste.Atualizar();
        t.Foto("36-devolucao-pix");

        tela.Busca = "999";
        tela.BuscarCommand.Execute(null);
        Assert.Equal("Pedido 999 não encontrado neste caixa.", t.Principal.Aviso);
    }

    [AvaloniaFact]
    public async Task Modo_teste_pelo_atalho_escondido_separa_e_apaga_as_vendas_de_teste()
    {
        using var t = new TelaDeTeste(configurar: c => c.SenhaMaster = "4321");
        // Caixa fechado: o técnico programa a máquina antes de entregar ao cliente
        var abertura = Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        for (var i = 0; i < 4; i++) abertura.ToqueNaMarcaCommand.Execute(null);
        Assert.Null(t.Principal.Dialogo); // 4 toques não fazem nada
        abertura.ToqueNaMarcaCommand.Execute(null);
        var senha = Assert.IsType<SenhaViewModel>(t.Principal.Dialogo);
        Assert.Equal("Modo teste", senha.Titulo);
        foreach (var d in "4321") senha.TeclaCommand.Execute(d.ToString());
        senha.ConfirmarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var confirmar = Assert.IsType<MensagemViewModel>(t.Principal.Dialogo);
        t.Foto("37-modo-teste-confirmar");
        confirmar.SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.ModoTeste);

        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.True(t.Principal.Sessao!.Teste);
        t.Principal.Aviso = null;
        var pedido = Vender(t, FormaPagamento.Dinheiro, ("PASTEL", 2));
        Assert.True(pedido.Teste);
        Assert.Equal(1, pedido.Numero);
        t.Tocar("PASTEL");
        t.Foto("38-modo-teste-venda");

        // Os relatórios do cliente não veem o caixa de teste
        Assert.Empty(t.Sistema.Caixa.Sessoes(DateTime.Today, DateTime.Today));
        // No menu, "Fechar caixa" vira "Sair do modo teste"
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        Assert.DoesNotContain(menu.Itens, i => i.Titulo == "Fechar caixa");
        menu.EscolherCommand.Execute(menu.Itens.Single(i => i.Titulo == "Sair do modo teste"));
        TelaDeTeste.Atualizar();
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => !t.Principal.ModoTeste);

        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Null(t.Principal.Sessao);
        Assert.Equal(0, t.Sistema.Banco.Escalar<long>("SELECT COUNT(*) FROM pedidos"));
        Assert.Equal(0, t.Sistema.Banco.Escalar<long>("SELECT COUNT(*) FROM sessoes"));
    }

    [AvaloniaFact]
    public async Task Modo_teste_com_caixa_aberto_volta_para_o_caixa_de_verdade()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa(5000);
        var real = t.Principal.Sessao!;
        Vender(t, FormaPagamento.Dinheiro, ("PASTEL", 1));

        // F1 com teclado ligado ao tablet (sem senha master não pede senha)
        t.Janela.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
        TelaDeTeste.Atualizar();
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.ModoTeste);
        Assert.NotEqual(real.Id, t.Principal.Sessao!.Id);

        // Se o programa fechar no meio do teste, volta no modo teste
        t.Principal.Iniciar();
        Assert.True(t.Principal.ModoTeste);

        var sair = t.Principal.SairDoModoTeste();
        TelaDeTeste.Atualizar();
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await sair;
        Assert.Equal(real.Id, t.Principal.Sessao!.Id);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.Equal(1000, t.Sistema.Caixa.Resumo(real.Id).TotalVendas);
    }

    [AvaloniaFact]
    public async Task Sangria_mostra_em_destaque_o_dinheiro_que_deve_estar_no_caixa()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa(5000);
        var pedido = Vender(t, FormaPagamento.Dinheiro, ("PASTEL", 3));
        t.Sistema.Devolucoes.Devolver(t.Principal.Sessao!, pedido.Id,
            new Dictionary<long, int> { [pedido.Itens[0].Id] = 1 }, null);
        var tela = new SangriaViewModel(t.Principal);
        t.Principal.Abrir(tela);

        Assert.Equal("R$ 70,00", tela.DinheiroEmCaixa);
        Assert.Contains(tela.Composicao, l => l.Nome == "− Fichas devolvidas" && l.Valor == "R$ 10,00");
        TelaDeTeste.Atualizar();
        var valor = t.Achar<TextBlock>(b => b.Text == "R$ 70,00");
        Assert.True(valor.FontSize >= 40);
        tela.Valor.SomarCommand.Execute("2000");
        Assert.Equal("Depois da sangria: R$ 50,00", tela.Depois);
        t.Foto("27-sangria");
        tela.Valor.SomarCommand.Execute("10000");
        Assert.True(tela.DepoisNegativo);
        tela.Valor.Centavos = 2000;
        await tela.SalvarCommand.ExecuteAsync(null);
        Assert.Equal("R$ 50,00", tela.DinheiroEmCaixa);
    }

    [AvaloniaFact]
    public void Seguranca_cria_senha_digitando_duas_vezes_e_trava_as_telas()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = 5;
        TelaDeTeste.Atualizar();
        Assert.True(tela.SemSenha);
        t.Foto("25-config-seguranca-sem-senha");

        tela.DefinirSenhaCommand.Execute(null);
        var nova = Assert.IsType<NovaSenhaViewModel>(t.Principal.Dialogo);
        foreach (var d in "12") nova.TeclaCommand.Execute(d.ToString());
        nova.ConfirmarCommand.Execute(null);
        Assert.Equal("Use pelo menos 4 números", nova.Erro);
        foreach (var d in "34") nova.TeclaCommand.Execute(d.ToString());
        nova.ConfirmarCommand.Execute(null);
        foreach (var d in "1235") nova.TeclaCommand.Execute(d.ToString());
        nova.ConfirmarCommand.Execute(null);
        Assert.Equal("As senhas não são iguais. Comece de novo.", nova.Erro);
        foreach (var senha in new[] { "1234", "1234" })
        {
            foreach (var d in senha) nova.TeclaCommand.Execute(d.ToString());
            nova.ConfirmarCommand.Execute(null);
        }
        Assert.Null(t.Principal.Dialogo);
        Assert.Equal("1234", t.Sistema.Config.Atual.SenhaMaster); // gravada na hora
        Assert.True(tela.TemSenha);

        tela.LiberarTudoCommand.Execute(null);
        tela.Protecoes.Single(p => p.Tela == TelaProtegida.Devolucao).Marcada = true;
        TelaDeTeste.Atualizar();
        t.Foto("25-config-seguranca");
        tela.SalvarCommand.Execute(null);
        Assert.Equal(TelaProtegida.Devolucao, t.Sistema.Config.Atual.TelasProtegidas);
    }

    [AvaloniaFact]
    public void Impressora_sem_botao_procurar_e_com_ajuste_de_posicao()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = 3;
        TelaDeTeste.Atualizar();
        Assert.DoesNotContain(t.Janela.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && Equals(b.Content, "Procurar"));

        tela.MoverImpressaoCommand.Execute("esquerda");
        tela.MoverImpressaoCommand.Execute("esquerda");
        tela.MoverImpressaoCommand.Execute("esquerda");
        Assert.Equal("1,5 mm para a esquerda", tela.AjusteTexto);
        TelaDeTeste.Atualizar();
        t.Foto("24-config-impressora");
        tela.SalvarCommand.Execute(null);
        Assert.Equal(-12, t.Sistema.Config.Atual.AjusteHorizontal);
        tela.MoverImpressaoCommand.Execute("centro");
        Assert.Equal("Centralizada", tela.AjusteTexto);

        tela.AbaSelecionada = 4;
        TelaDeTeste.Atualizar();
        t.Foto("24b-config-maquininha");
    }

    [AvaloniaFact]
    public async Task Imagem_do_produto_abre_na_pasta_das_fotos()
    {
        using var t = new TelaDeTeste();
        (string? Pasta, bool Avisar)? pedido = null;
        t.Principal.EscolherImagem = (pasta, avisar) =>
        {
            pedido = (pasta, avisar);
            return Task.FromResult<string?>(null);
        };
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        await tela.EscolherImagemCommand.ExecuteAsync(null);
        Assert.Equal((@"C:\Sistema_New\produtos", true), pedido);
    }

    [AvaloniaFact]
    public async Task Pedido_que_ficou_esperando_a_maquininha_separada_pergunta_ao_abrir_o_programa()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var carrinho = new Carrinho();
        carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL"), 2);
        var pendente = t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, FormaPagamento.Debito);
        var outro = t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, FormaPagamento.Pix);

        // O programa abre de novo (ex.: acabou a energia no meio do pagamento)
        t.Principal.Iniciar();
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        var pergunta = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Contains($"O pedido {pendente.Numero} (R$ 20,00 no Débito)", pergunta.Texto);
        t.Foto("39-pagamento-sem-resposta");
        pergunta.SimCommand.Execute(null);
        Assert.Equal(2, t.EsperarImpressoes(2).Length);

        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel m && m != pergunta);
        ((MensagemViewModel)t.Principal.Dialogo!).NaoCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Sistema.Vendas.Pendentes().Count == 0);

        Assert.Equal(StatusPedido.Pago, t.Sistema.Vendas.Pedido(pendente.Id)!.Status);
        Assert.Equal(1, t.Sistema.Vendas.Pedido(pendente.Id)!.Impressoes);
        Assert.Equal(StatusPedido.Cancelado, t.Sistema.Vendas.Pedido(outro.Id)!.Status);
    }

    [AvaloniaFact]
    public void Abertura_tem_a_marca_da_bc_fichas()
    {
        using var t = new TelaDeTeste();
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "BC-FICHAS" && b.IsEffectivelyVisible);
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<TextBlock>(),
            b => b.Text == "Suporte (19) 99821-6489" && b.IsEffectivelyVisible);
    }
}
