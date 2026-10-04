using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Auditoria das telas: toques duplos de verdade (com o dedo, não pelo comando), corridas enquanto grava, tela
/// pequena (1024 x 600) e com zoom, idas e voltas e campos com valores estranhos.
/// </summary>
public class AuditoriaDasTelasTests
{
    /// <summary>Toque de verdade num ponto da janela (aperta e solta, como o dedo no tablet).</summary>
    private static void TocarEm(TelaDeTeste t, Point ponto)
    {
        t.Janela.MouseDown(ponto, MouseButton.Left);
        t.Janela.MouseUp(ponto, MouseButton.Left);
        TelaDeTeste.Atualizar();
    }

    private static Point Centro(TelaDeTeste t, Control alvo) =>
        alvo.TranslatePoint(new Point(alvo.Bounds.Width / 2, alvo.Bounds.Height / 2), t.Janela)!.Value;

    private static bool TemTexto(Control b, string texto) =>
        b is Button { Content: string s } && s == texto ||
        b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == texto && x.IsEffectivelyVisible);

    private static Button Botao(TelaDeTeste t, string texto) =>
        t.Janela.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && TemTexto(b, texto));

    private static Button BotaoDoComando(TelaDeTeste t, System.Windows.Input.ICommand comando, object? parametro = null) =>
        t.Janela.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && b.Command == comando && (parametro is null || Equals(b.CommandParameter, parametro)));

    // ---------- Toque duplo: o segundo toque não pode cair na tela que acabou de abrir ----------

    [AvaloniaFact]
    public async Task Toque_duplo_no_abrir_caixa_nao_poe_produto_no_pedido()
    {
        using var t = new TelaDeTeste();
        var abrir = Centro(t, Botao(t, "Abrir caixa"));
        TocarEm(t, abrir);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        // O segundo toque do dedo cai em cima de um produto da tela de venda
        TocarEm(t, abrir);
        Assert.True(t.Venda.Vazio, "o segundo toque do Abrir caixa pôs no pedido: " + t.Venda.Total);

        // Passado o instante do toque duplo, tocar no produto vale normalmente
        await Task.Delay(400);
        TocarEm(t, abrir);
        Assert.False(t.Venda.Vazio);
    }

    [AvaloniaFact]
    public void Toque_duplo_no_dinheiro_nao_troca_o_valor_da_venda()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var dinheiro = Centro(t, Botao(t, "Dinheiro"));
        TocarEm(t, dinheiro);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        Assert.True(pagamento.EmDinheiro);
        // Embaixo do dedo agora está a nota de R$ 5: antes o valor da venda virava R$ 5,00 ("Falta R$ 5,00")
        TocarEm(t, dinheiro);
        Assert.Equal("R$ 10,00", pagamento.Recebido.Texto);
        Assert.True(pagamento.Recebido.Sugerido);
        Assert.False(pagamento.FaltaDinheiro);
    }

    [AvaloniaFact]
    public void Toque_duplo_no_menos_da_ultima_unidade_nao_tira_o_item_de_baixo()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        t.Tocar("ESPETINHO");
        TelaDeTeste.Atualizar();
        var menos = Centro(t, BotaoDoComando(t, t.Venda.MenosCommand, t.Venda.Linhas[0]));
        TocarEm(t, menos);
        Assert.Equal("ESPETINHO", Assert.Single(t.Venda.Linhas).Nome);
        // A linha do ESPETINHO subiu para baixo do dedo: o segundo toque não pode tirar um espetinho
        TocarEm(t, menos);
        Assert.Equal(2, t.Venda.Linhas.Single().Quantidade);
    }

    [AvaloniaFact]
    public async Task Dois_toques_no_mesmo_botao_e_teclas_seguidas_continuam_valendo()
    {
        using var t = new TelaDeTeste();
        // Teclas seguidas, bem rápido, no teclado da abertura
        var abertura = Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        var teclado = t.Achar<TecladoNumerico>();
        foreach (var tecla in new[] { "1", "2", "3", "4", "5" })
            TocarEm(t, Centro(t, teclado.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, tecla))));
        Assert.Equal("R$ 123,45", abertura.Troco.Texto);
        TocarEm(t, Centro(t, Botao(t, "+ R$ 5")));
        TocarEm(t, Centro(t, Botao(t, "+ R$ 5")));
        Assert.Equal("R$ 133,45", abertura.Troco.Texto);

        t.AbrirCaixa();
        await Task.Delay(400); // a tela trocou: espera o instante do toque duplo passar
        TelaDeTeste.Atualizar();
        // Dois toques rápidos no mesmo produto: duas unidades
        var pastel = Centro(t, Botao(t, "PASTEL"));
        TocarEm(t, pastel);
        TocarEm(t, pastel);
        Assert.Equal(2, t.Venda.Linhas.Single().Quantidade);
        // E no + da linha também
        var mais = Centro(t, BotaoDoComando(t, t.Venda.MaisCommand));
        TocarEm(t, mais);
        TocarEm(t, mais);
        Assert.Equal(4, t.Venda.Linhas.Single().Quantidade);
    }

    // ---------- Pagamento ----------

    [AvaloniaFact]
    public async Task Enquanto_grava_a_venda_em_dinheiro_voltar_e_pix_nao_criam_outro_pedido()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        var confirmar = pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        // O tablet ainda está gravando a venda e o operador toca em Voltar, PIX e no X
        Assert.False(pagamento.PodeFechar);
        pagamento.OutraFormaCommand.Execute(null);
        if (pagamento.PixCommand.CanExecute(null)) pagamento.PixCommand.Execute(null);
        pagamento.FecharCommand.Execute(null);
        await confirmar;
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is null);

        // Antes: o PIX virava um segundo pedido, esperando a maquininha para sempre, e o caixa não fechava mais
        var pedido = Assert.Single(t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id));
        Assert.Equal(FormaPagamento.Dinheiro, pedido.Forma);
        Assert.Equal(StatusPedido.Pago, pedido.Status);
        Assert.Empty(t.Sistema.Vendas.Pendentes());
        t.Sistema.Caixa.Fechar(t.Principal.Sessao!, null);
    }

    [AvaloniaFact]
    public async Task Troco_da_venda_anterior_some_quando_a_proxima_e_paga()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("2000");
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        Assert.True(t.Venda.MostrarTroco);
        Assert.Equal("R$ 10,00", t.Venda.UltimoTroco);

        // Começar o próximo pedido não esconde o troco (o operador ainda está devolvendo o dinheiro)
        t.Tocar("PASTEL");
        Assert.True(t.Venda.MostrarTroco);

        // Mas a próxima venda, paga certinho dentro dos 5 segundos, não pode ficar com o troco da anterior na tela
        t.Venda.PagarCommand.Execute(null);
        pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        Assert.False(t.Venda.MostrarTroco, "ficou na tela o troco da venda anterior: " + t.Venda.UltimoTroco);

        // Uma venda com troco depois mostra o troco dela
        t.Tocar("ESPETINHO");
        t.Venda.PagarCommand.Execute(null);
        pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("5000");
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        Assert.True(t.Venda.MostrarTroco);
        Assert.Equal("R$ 38,00", t.Venda.UltimoTroco);
    }

    // ---------- Produtos ----------

    [AvaloniaFact]
    public void Estoque_digitado_errado_avisa_em_vez_de_virar_zero()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Selecionado = tela.Lista.First(p => p.Nome == "PASTEL");
        tela.ControlaEstoque = true;

        // "1.000" (com o ponto de milhar) ou "10,5" viravam 0 e o produto aparecia ESGOTADO na venda
        foreach (var errado in new[] { "1.000", "10,5", "abc", "99999999999" })
        {
            tela.Estoque = errado;
            tela.SalvarCommand.Execute(null);
            Assert.True(t.Principal.AvisoErro, errado);
            Assert.Equal("Digite a quantidade em estoque só com números, por exemplo 100.", t.Principal.Aviso);
            Assert.False(t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL").ControlaEstoque);
        }

        tela.Estoque = "1000";
        tela.SalvarCommand.Execute(null);
        var pastel = t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        Assert.True(pastel.ControlaEstoque);
        Assert.Equal(1000, pastel.Estoque);

        // Sem controlar o estoque o campo é ignorado, como antes
        tela.ControlaEstoque = false;
        tela.Estoque = "abc";
        tela.SalvarCommand.Execute(null);
        Assert.False(t.Principal.AvisoErro);
    }

    [AvaloniaFact]
    public async Task Produto_de_preco_zero_vende_em_dinheiro_e_no_pix()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Nome = "Cortesia";
        tela.Preco = "0";
        tela.SalvarCommand.Execute(null);
        t.Principal.IrParaVenda();
        TelaDeTeste.Atualizar();

        t.Tocar("CORTESIA");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        Assert.False(pagamento.FaltaDinheiro);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is null);

        t.Tocar("CORTESIA");
        t.Venda.PagarCommand.Execute(null);
        pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        var pix = pagamento.PixCommand.ExecuteAsync(null);
        await Task.Delay(700); // o APROVADO só vale depois do primeiro meio segundo
        pagamento.ConfirmarNaMaquininhaCommand.Execute(null);
        await pix;
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is null);
        Assert.Equal(2, t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id).Count(p => p.Status == StatusPedido.Pago));
        Assert.Empty(t.Sistema.Vendas.Pendentes());
    }

    // ---------- Tela pequena e zoom ----------

    /// <summary>Textos sem quebra nem reticências que não cabem no lugar deles (aparecem cortados).</summary>
    private static List<string> Cortados(TelaDeTeste t)
    {
        TelaDeTeste.Atualizar();
        return t.Janela.GetVisualDescendants().OfType<TextBlock>()
            .Where(x => x.IsEffectivelyVisible && !string.IsNullOrEmpty(x.Text) && x.Bounds.Width > 0
                        && x.TextTrimming == TextTrimming.None && x.TextWrapping == TextWrapping.NoWrap
                        // A caixa de escolha mostra o texto inteiro ao abrir a lista
                        && x.FindAncestorOfType<ComboBox>() is null
                        && x.TextLayout.WidthIncludingTrailingWhitespace > x.Bounds.Width + 1)
            .Select(x => $"{x.Text} ({x.TextLayout.WidthIncludingTrailingWhitespace:0} > {x.Bounds.Width:0})")
            .ToList();
    }

    [AvaloniaTheory]
    [InlineData(1024, 600, 100)]
    [InlineData(1280, 800, 100)]
    [InlineData(1280, 800, 125)]
    public async Task Valores_e_botoes_aparecem_inteiros_em_todas_as_telas(int largura, int altura, int zoom)
    {
        var cortados = new List<string>();
        var foto = largura == 1024;
        using var t = new TelaDeTeste(largura, altura, c =>
        {
            c.Zoom = zoom;
            c.LiberarDevolucao = true;
            c.LiberarReimpressao = true;
        });
        void Ver(string tela)
        {
            cortados.AddRange(Cortados(t).Select(x => tela + ": " + x));
            if (foto) t.Foto("auditoria-1024x600-" + tela);
        }

        // Abertura: o troco de R$ 100,00 aparecia "R$ 100,0" e os botões rápidos todos como "+ R$"
        var abertura = Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        abertura.Troco.Centavos = 150000;
        Ver("abertura");
        t.AbrirCaixa(10000);
        foreach (var p in new[] { "PASTEL", "ESPETINHO", "CACHORRO-QUENTE", "PORÇÃO DE BATATA", "PIZZA (FATIA)", "CALDO" })
            t.Tocar(p);
        Ver("venda");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        Ver("pagamento");
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("10000");
        Ver("pagamento-dinheiro");
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        Ver("pagamento-concluido");
        pagamento.FecharCommand.Execute(null);
        // O "Recebido R$ 100,00" do troco aparecia "Recebido R"
        Ver("venda-troco");
        t.Venda.AbrirMenuCommand.Execute(null);
        Ver("menu");
        t.Principal.FecharTodosDialogos();
        foreach (var pagina in new PaginaViewModel[]
                 {
                     new ProdutosViewModel(t.Principal), new DevolucaoViewModel(t.Principal),
                     new ReimpressaoViewModel(t.Principal), new RelatoriosViewModel(t.Principal),
                     new FechamentoViewModel(t.Principal),
                 })
        {
            t.Principal.Abrir(pagina);
            Ver(pagina.GetType().Name.Replace("ViewModel", "").ToLowerInvariant());
        }
        // Sangria: as notas apareciam "R$ 1" para R$ 1, R$ 10 e R$ 100
        var sangria = new SangriaViewModel(t.Principal);
        t.Principal.Abrir(sangria);
        sangria.Valor.Centavos = 123456;
        Ver("sangria");
        var configuracao = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(configuracao);
        for (var aba = 0; aba <= ConfiguracaoViewModel.AbaMaquina; aba++)
        {
            configuracao.AbaSelecionada = aba;
            configuracao.MaquinaLiberada = aba == ConfiguracaoViewModel.AbaMaquina;
            Ver("configuracao-" + aba);
        }
        Assert.True(cortados.Count == 0, string.Join("\n", cortados));
    }

    /// <summary>Botões (fora de áreas com rolagem) que ficam, mesmo em parte, fora da janela.</summary>
    private static List<string> ForaDaJanela(TelaDeTeste t)
    {
        TelaDeTeste.Atualizar();
        return t.Janela.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Bounds.Height > 0 && b.FindAncestorOfType<ScrollViewer>() is null)
            .Select(b => (Botao: b, Inicio: b.TranslatePoint(default, t.Janela)!.Value,
                Fim: b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), t.Janela)!.Value))
            .Where(x => x.Inicio.X < -1 || x.Inicio.Y < -1 || x.Fim.X > t.Janela.Bounds.Width + 1 ||
                        x.Fim.Y > t.Janela.Bounds.Height + 1)
            .Select(x => $"{x.Botao.Content as string ?? x.Botao.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text} " +
                         $"({x.Inicio.Y:0} a {x.Fim.Y:0})")
            .ToList();
    }

    [AvaloniaTheory]
    [InlineData(1024, 600, 100)]
    [InlineData(1280, 800, 125)]
    public async Task Janelas_por_cima_cabem_inteiras_na_tela(int largura, int altura, int zoom)
    {
        var fora = new List<string>();
        using var t = new TelaDeTeste(largura, altura, c => c.Zoom = zoom);
        void Ver(string tela) => fora.AddRange(ForaDaJanela(t).Select(x => tela + ": " + x));
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        Ver("pagamento");
        pagamento.EscolherDinheiroCommand.Execute(null);
        Ver("pagamento-dinheiro");
        pagamento.OutraFormaCommand.Execute(null);
        var debito = pagamento.DebitoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => pagamento.EmMaquininha);
        Ver("pagamento-maquininha");
        pagamento.CancelarMaquininhaCommand.Execute(null);
        await debito;
        pagamento.FecharCommand.Execute(null);
        t.Venda.AbrirMenuCommand.Execute(null);
        Ver("menu");
        t.Principal.FecharTodosDialogos();
        t.Principal.PedirSenha(() => { });
        ((SenhaViewModel)t.Principal.Dialogo!).Erro = "Senha errada";
        Ver("senha");
        t.Principal.FecharTodosDialogos();
        // A mensagem mais comprida do programa: backup feito, com pendrives, modo teste e estoque
        _ = t.Principal.Confirmar("Apagar as vendas desta máquina?",
            "Backup feito. Salvo em:\nC:\\Sistema_New\\backup\\BCFichas - FESTA JUNINA DA ESCOLA ESTADUAL SÃO JOÃO.bcf" +
            "\ne no pendrive E:\\\ne no pendrive F:\\\nNão consegui copiar para o pendrive G:\\." +
            "\n\nVai só a programação (produtos, combos, fotos, logotipo, ficha e senha), nunca as vendas. Na outra " +
            "máquina: ponha o arquivo na pasta do backup dela (ou o pendrive) e toque em Restaurar.\n\nEsta máquina " +
            "ainda tem 120 pedido(s) + 5 de teste em 3 caixa(s). Apagar agora, para ela também ir pura para o cliente?" +
            "\n\nO modo teste é desligado.\nO que as vendas tiraram do estoque (37 unidade(s)) volta para ele.",
            "Apagar as vendas", "Agora não", perigo: true);
        Ver("mensagem-comprida");
        t.Principal.FecharTodosDialogos();
        var configuracao = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(configuracao);
        configuracao.GradeDaAbaCommand.Execute(configuracao.Abas[0]);
        Ver("grade-da-aba");
        t.Principal.FecharTodosDialogos();
        t.Principal.AbrirDialogo(new NovaSenhaViewModel(t.Principal, _ => { }));
        Ver("nova-senha");
        t.Principal.FecharTodosDialogos();
        t.Principal.AbrirDialogo(new NumeroViewModel(t.Principal, "Número deste caixa (PDV)",
            "Cada máquina do evento tem um número diferente (sai na ficha e nos relatórios).", 1, 30));
        Ver("numero");
        Assert.True(fora.Count == 0, string.Join("\n", fora));
    }

    // ---------- Idas e voltas ----------

    [AvaloniaFact]
    public void Idas_e_voltas_50_vezes_nao_acumulam_janelas_nem_perdem_o_pedido()
    {
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.LiberarDevolucao = true;
            c.LiberarReimpressao = true;
        });
        t.AbrirCaixa();
        for (var i = 0; i < 50; i++)
        {
            t.Tocar("PASTEL");
            t.Venda.PagarCommand.Execute(null);
            var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
            pagamento.EscolherDinheiroCommand.Execute(null);
            pagamento.OutraFormaCommand.Execute(null);
            pagamento.FecharCommand.Execute(null);
            Assert.Null(t.Principal.Dialogo);

            // Cada tela do menu (menos Configurações e Sair) e o Voltar, que traz o menu de volta
            t.Venda.AbrirMenuCommand.Execute(null);
            var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
            menu.EscolherCommand.Execute(menu.Itens[i % (menu.Itens.Count - 2)]);
            TelaDeTeste.Atualizar();
            Assert.IsAssignableFrom<PaginaViewModel>(t.Principal.Pagina).VoltarCommand.Execute(null);
            TelaDeTeste.Atualizar();
            Assert.IsType<MenuViewModel>(t.Principal.Dialogo).FecharCommand.Execute(null);
            Assert.Null(t.Principal.Dialogo);

            // Barra lateral
            t.Venda.AbrirSangriaCommand.Execute(null);
            Assert.IsType<SangriaViewModel>(t.Principal.Pagina).VoltarCommand.Execute(null);
            t.Venda.FecharCaixaCommand.Execute(null);
            Assert.IsType<FechamentoViewModel>(t.Principal.Pagina).VoltarCommand.Execute(null);
            TelaDeTeste.Atualizar();
            Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        }
        Assert.Equal(50, t.Venda.Linhas.Single().Quantidade);
        Assert.Equal(6, t.Venda.Botoes.Count);
        var dialogos = (System.Collections.IList)typeof(PrincipalViewModel)
            .GetField("_dialogos", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(t.Principal)!;
        Assert.Empty(dialogos);
        Assert.Empty(t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id));
    }

    [AvaloniaFact]
    public async Task F1_liga_e_desliga_o_modo_teste_com_o_caixa_aberto()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var caixa = t.Principal.Sessao!;
        t.Tocar("PASTEL");
        t.Janela.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.ModoTeste);
        Assert.NotEqual(caixa.Id, t.Principal.Sessao!.Id);
        Assert.True(t.Venda.Vazio);

        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        // Com uma janela aberta o F1 não faz nada
        t.Janela.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.Same(pagamento, t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);

        t.Janela.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => !t.Principal.ModoTeste);
        Assert.Equal(caixa.Id, t.Principal.Sessao!.Id);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.Empty(t.Sistema.Vendas.Pedidos(caixa.Id));
    }

    [AvaloniaFact]
    public void Relatorios_com_40_caixas_e_reimpressao_sem_pedidos_funcionam()
    {
        using var t = new TelaDeTeste(1024, 600);
        for (var i = 0; i < 40; i++)
        {
            var sessao = t.Sistema.Caixa.Abrir(1, null, 1000);
            var carrinho = new Carrinho();
            carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First());
            t.Sistema.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 5000);
            t.Sistema.Caixa.Fechar(sessao, i % 2 == 0 ? 2000 : null);
        }
        t.AbrirCaixa();
        var relatorios = new RelatoriosViewModel(t.Principal);
        t.Principal.Abrir(relatorios);
        relatorios.AbaSelecionada = 1;
        Assert.Equal(41, relatorios.Sessoes.Count);
        relatorios.FiltrarCommand.Execute("0");
        relatorios.SessaoSelecionada = relatorios.Sessoes.Last();
        Assert.Equal("R$ 10,00", relatorios.ResumoSelecionado!.TotalVendido);
        relatorios.AbaSelecionada = 2;
        TelaDeTeste.Atualizar();

        var reimpressao = new ReimpressaoViewModel(t.Principal);
        t.Principal.Abrir(reimpressao);
        Assert.True(reimpressao.Vazio);
        reimpressao.Busca = "abc";
        reimpressao.Busca = "#99999";
        reimpressao.ReimprimirTudoCommand.Execute(null);
        Assert.False(reimpressao.PodeReimprimir);
        Assert.Null(t.Principal.Aviso);
    }
}
