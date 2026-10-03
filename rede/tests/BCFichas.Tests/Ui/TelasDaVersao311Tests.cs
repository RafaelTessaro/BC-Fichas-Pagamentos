using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Versão 3.11: troco na tela de venda, Sangria e Fechar caixa na barra lateral, valor recebido já preenchido,
/// devolução só em dinheiro e a aba Máquina com a senha técnica.
/// </summary>
public class TelasDaVersao311Tests
{
    private static List<Button> Visiveis(TelaDeTeste t, Func<Button, bool> filtro) =>
        t.Janela.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && filtro(b)).ToList();

    private static bool TemTexto(Button b, string texto) =>
        Equals(b.Content, texto) || b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == texto && x.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task Dinheiro_ja_vem_com_o_valor_da_venda_e_digitar_troca_o_valor()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);

        // Pagou certinho: já está lá, é só confirmar
        Assert.Equal("R$ 20,00", pagamento.Recebido.Texto);
        Assert.True(pagamento.Recebido.Sugerido);
        Assert.False(pagamento.FaltaDinheiro);
        Assert.Equal("R$ 0,00", pagamento.Troco);
        TelaDeTeste.Atualizar();
        t.Foto("05-pagamento-dinheiro");

        // Sem "Valor exato" e sem "00": o Limpar fica no lugar do 00
        var janela = t.Achar<PagamentoView>();
        Assert.DoesNotContain(janela.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && (TemTexto(b, "Valor exato") || TemTexto(b, "00")));
        var limpar = Assert.Single(janela.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && TemTexto(b, "Limpar"));
        Assert.Equal("C", limpar.CommandParameter);

        // O cliente deu R$ 30: a primeira tecla apaga o valor e começa do zero
        foreach (var tecla in new[] { "3", "0", "0", "0" }) pagamento.Recebido.TeclaCommand.Execute(tecla);
        Assert.Equal("R$ 30,00", pagamento.Recebido.Texto);
        Assert.False(pagamento.Recebido.Sugerido);
        Assert.Equal("R$ 10,00", pagamento.Troco);
        limpar.Command!.Execute(limpar.CommandParameter);
        Assert.Equal("Falta R$ 20,00", pagamento.Troco); // a falta continua aparecendo embaixo
        Assert.True(pagamento.FaltaDinheiro);

        // As notas também trocam o valor da venda (R$ 50 = R$ 50, não R$ 70)
        pagamento.OutraFormaCommand.Execute(null);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("5000");
        Assert.Equal("R$ 50,00", pagamento.Recebido.Texto);
        Assert.Equal("R$ 30,00", pagamento.Troco);

        // Confirmou: o pagamento fecha sozinho e o troco fica uns segundos em cima do pedido
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is null);
        Assert.True(t.Venda.MostrarTroco);
        Assert.Equal("R$ 30,00", t.Venda.UltimoTroco);
        Assert.Equal("Recebido R$ 50,00", t.Venda.UltimoRecebido);
        TelaDeTeste.Atualizar();
        var troco = t.Achar<TextBlock>(x => x.Text == "R$ 30,00" && x.IsEffectivelyVisible);
        // Fica no cartão do pedido (à direita), sem tapar os botões dos produtos
        Assert.True(troco.TranslatePoint(new Point(0, 0), t.Janela)!.Value.X > 800);
        t.Foto("57-venda-ultimo-troco");

        // Começar o próximo pedido não esconde o troco; depois de uns 5 segundos ele some sozinho
        t.Tocar("PASTEL");
        Assert.True(t.Venda.MostrarTroco);
        await TelaDeTeste.Esperar(() => !t.Venda.MostrarTroco, 8000);
    }

    [AvaloniaFact]
    public async Task Sem_troco_nao_mostra_nada_e_cartao_tambem_nao()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null); // "Próximo cliente" antes de fechar sozinho
        Assert.Null(t.Principal.Dialogo);
        Assert.False(t.Venda.MostrarTroco);
    }

    [AvaloniaFact]
    public void Barra_lateral_tem_sangria_e_fechar_caixa_e_nao_mostra_impressora_nem_maquininha()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        TelaDeTeste.Atualizar();
        var sangria = Assert.Single(Visiveis(t, b => b.Command == t.Venda.AbrirSangriaCommand));
        var fechar = Assert.Single(Visiveis(t, b => b.Command == t.Venda.FecharCaixaCommand));
        Assert.True(TemTexto(fechar, "Fechar caixa"));
        // Um embaixo do outro, na largura toda da barra: botões grandes para o dedo
        Assert.All(new[] { sangria, fechar }, b => Assert.True(b.Bounds.Width >= 110 && b.Bounds.Height >= 56,
            $"{b.Bounds.Width:0} × {b.Bounds.Height:0}"));
        Assert.True(fechar.TranslatePoint(new Point(0, 0), t.Janela)!.Value.Y >
                    sangria.TranslatePoint(new Point(0, sangria.Bounds.Height), t.Janela)!.Value.Y);
        // Na lateral esquerda, embaixo das abas e em cima do relógio
        Assert.True(sangria.TranslatePoint(new Point(0, 0), t.Janela)!.Value.X < 136);
        var relogio = t.Achar<TextBlock>(x => x.Text == t.Principal.Relogio && x.IsEffectivelyVisible);
        Assert.True(fechar.TranslatePoint(new Point(0, fechar.Bounds.Height), t.Janela)!.Value.Y <=
                    relogio.TranslatePoint(new Point(0, 0), t.Janela)!.Value.Y);
        // A impressora e a maquininha saíram da lateral (o suporte e a versão ficam)
        var textos = t.Janela.GetVisualDescendants().OfType<TextBlock>().Where(x => x.IsEffectivelyVisible)
            .Select(x => x.Text ?? "").ToList();
        Assert.DoesNotContain(textos, x => x.Contains("Maquininha", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(textos, x => x.Contains(t.Sistema.Impressao.Descricao(), StringComparison.Ordinal));
        Assert.Contains(textos, x => x == t.Principal.Versao);
        // O telefone do suporte saiu da lateral e foi para o menu
        Assert.DoesNotContain(textos, x => x.Contains(Configuracao.Suporte, StringComparison.Ordinal));

        // Sangria abre direto (o Voltar volta para a venda, sem o menu)
        sangria.Command!.Execute(null);
        var tela = Assert.IsType<SangriaViewModel>(t.Principal.Pagina);
        tela.VoltarCommand.Execute(null);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.Null(t.Principal.Dialogo);

        t.Venda.FecharCaixaCommand.Execute(null);
        Assert.IsType<FechamentoViewModel>(t.Principal.Pagina);

        // Sangria e Fechar caixa saíram do menu
        t.Principal.IrParaVenda();
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        Assert.DoesNotContain(menu.Itens, i => i.Titulo is "Sangria / Suprimento" or "Fechar caixa");
        TelaDeTeste.Atualizar();
        t.Achar<TextBlock>(x => x.Text == "Suporte BC Fichas: " + Configuracao.Suporte);
    }

    [AvaloniaFact]
    public void Barra_lateral_com_senha_pede_a_senha_na_sangria()
    {
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.SenhaMaster = "1234";
            c.TelasProtegidas |= TelaProtegida.Sangria;
        });
        t.AbrirCaixa();
        t.Venda.AbrirSangriaCommand.Execute(null);
        Assert.IsType<SenhaViewModel>(t.Principal.Dialogo);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
    }

    [AvaloniaFact]
    public void Menu_so_mostra_devolucao_e_reimpressao_quando_a_bc_fichas_libera()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        Assert.Equal(["Produtos", "Relatórios", "Configurações", "Sair do programa"], menu.Itens.Select(i => i.Titulo));
        TelaDeTeste.Atualizar();
        t.Foto("10-menu-padrao");
    }

    [AvaloniaFact]
    public async Task Devolucao_so_em_dinheiro_digita_o_valor_e_sai_do_caixa_como_sangria()
    {
        using var t = new TelaDeTeste(configurar: c => c.LiberarDevolucao = true);
        t.AbrirCaixa(10000);
        t.Tocar("PORÇÃO DE BATATA");
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        t.EsperarImpressoes(2);

        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        menu.EscolherCommand.Execute(menu.Itens.Single(i => i.Titulo == "Devolver fichas"));
        var tela = Assert.IsType<DevolucaoViewModel>(t.Principal.Pagina);
        Assert.Equal("R$ 130,00", tela.DinheiroEmCaixa);
        Assert.False(tela.PodeDevolver);

        // O cliente devolveu fichas de R$ 80: só o valor, sem o número da venda
        foreach (var tecla in new[] { "8", "0", "0", "0" }) tela.Valor.TeclaCommand.Execute(tecla);
        Assert.Equal("R$ 80,00", tela.Valor.Texto);
        Assert.Equal("Devolver R$ 80,00 ao cliente", tela.TextoBotao);
        Assert.Equal("Depois da devolução: R$ 50,00 no caixa", tela.Depois);
        Assert.True(tela.PodeDevolver);
        t.Principal.Aviso = null;
        TelaDeTeste.Atualizar();
        t.Foto("34-devolucao-dinheiro");

        foreach (var arquivo in Directory.GetFiles(t.PastaImpressoes, "*.png")) File.Delete(arquivo);
        await tela.RegistrarCommand.ExecuteAsync(null);
        Assert.Single(t.EsperarImpressoes(1)); // comprovante da devolução
        Assert.Equal("Devolução de R$ 80,00 registrada.", t.Principal.Aviso);
        Assert.Equal("R$ 0,00", tela.Valor.Texto);
        Assert.Equal("R$ 50,00", tela.DinheiroEmCaixa);
        Assert.Equal("R$ 80,00", tela.TotalDevolvido);
        Assert.Single(tela.Feitas);
        t.Principal.Aviso = null;
        TelaDeTeste.Atualizar();
        t.Foto("35-devolucao-feita");

        // Mais do que tem na gaveta: não deixa
        foreach (var tecla in new[] { "6", "0", "0", "0" }) tela.Valor.TeclaCommand.Execute(tecla);
        Assert.Equal("Não dá: no caixa só há R$ 50,00", tela.Depois);
        Assert.False(tela.PodeDevolver);

        // No caixa: sai da gaveta como uma sangria, mas no total de devoluções (e tira da venda líquida)
        var resumo = t.Sistema.Caixa.Resumo(t.Principal.Sessao!.Id);
        Assert.Equal(0, resumo.Sangrias);
        Assert.Equal(8000, resumo.TotalDevolvido);
        Assert.Equal(1, resumo.QuantidadeDevolucoes);
        Assert.Equal(3000 - 8000, resumo.VendaLiquida);
        Assert.Equal(5000, resumo.DinheiroEsperado);
        var fechamento = new ResumoVM(resumo);
        Assert.Contains(fechamento.Gaveta, l => l.Nome == "− Devoluções" && l.Valor == "R$ 80,00");
        Assert.Contains(fechamento.Devolucoes, l => l.Nome == "Dinheiro" && l.Valor == "− R$ 80,00");
    }

    [AvaloniaFact]
    public async Task Aba_maquina_pede_a_senha_tecnica_toda_vez_menos_no_modo_teste()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = ConfiguracaoViewModel.AbaMaquina;
        TelaDeTeste.Atualizar();
        Assert.False(tela.MaquinaLiberada);
        // Trancada: nada da aba aparece, só o pedido da senha
        Assert.Empty(Visiveis(t, b => b.Command == tela.FazerBackupCommand || b.Command == tela.ZerarProgramacaoCommand));
        var campo = t.Achar<TextBox>(x => x.Name == "CampoSenhaTecnica");
        Assert.True(campo.IsEffectivelyVisible);
        Assert.Equal('●', campo.PasswordChar);
        t.Foto("59-config-maquina-senha");

        tela.SenhaTecnicaDigitada = "1234";
        await tela.LiberarMaquinaCommand.ExecuteAsync(null);
        Assert.False(tela.MaquinaLiberada);
        Assert.Equal("Senha técnica errada.", tela.ErroSenhaTecnica);
        Assert.Equal("", tela.SenhaTecnicaDigitada);

        // Sem diferença de maiúsculas (o teclado da tela pode digitar em maiúsculas)
        tela.SenhaTecnicaDigitada = TelaDeTeste.SenhaTecnica.ToUpperInvariant() + " ";
        await tela.LiberarMaquinaCommand.ExecuteAsync(null);
        Assert.True(tela.MaquinaLiberada);
        Assert.Equal("", tela.ErroSenhaTecnica);
        TelaDeTeste.Atualizar();
        Assert.Single(Visiveis(t, b => b.Command == tela.FazerBackupCommand));

        // Liberar devolução e reimpressão para o cliente: vale ao salvar, como o resto
        Assert.False(tela.TemAlteracoes);
        tela.LiberarDevolucao = true;
        tela.LiberarReimpressao = true;
        Assert.True(tela.TemAlteracoes);
        TelaDeTeste.Atualizar();
        Assert.False(t.Principal.TecladoVisivel);
        t.Foto("60-config-maquina-liberar");
        tela.SalvarCommand.Execute(null);
        Assert.True(t.Sistema.Config.Atual.LiberarDevolucao);
        Assert.True(t.Sistema.Config.Atual.LiberarReimpressao);

        // Saiu da aba: na volta pede de novo
        tela.AbaSelecionada = 0;
        tela.AbaSelecionada = ConfiguracaoViewModel.AbaMaquina;
        Assert.False(tela.MaquinaLiberada);

        // A senha da BC Fichas não é a master nem a dos testes
        Assert.False(SenhaTecnica.Padrao.Confere(TelaDeTeste.SenhaTecnica));
        Assert.False(SenhaTecnica.Padrao.Confere(""));
    }

    [AvaloniaFact]
    public async Task No_modo_teste_a_aba_maquina_abre_sem_senha()
    {
        using var t = new TelaDeTeste();
        var entrar = t.Principal.EntrarNoModoTeste();
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await entrar;
        Assert.True(t.Principal.ModoTeste);

        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = ConfiguracaoViewModel.AbaMaquina;
        Assert.True(tela.MaquinaLiberada);
        tela.AbaSelecionada = 0;
        tela.AbaSelecionada = ConfiguracaoViewModel.AbaMaquina;
        Assert.True(tela.MaquinaLiberada);

        // No modo teste o botão da lateral sai do teste em vez de fechar o caixa
        t.Principal.IrParaVenda();
        TelaDeTeste.Atualizar();
        var fechar = Assert.Single(Visiveis(t, b => b.Command == t.Venda.FecharCaixaCommand));
        Assert.True(TemTexto(fechar, "Sair do teste"));
    }

    [AvaloniaFact]
    public void Reprogramar_para_novo_evento_desliga_as_opcoes_liberadas()
    {
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.LiberarDevolucao = true;
            c.LiberarReimpressao = true;
        });
        t.Sistema.Programacao.ZerarProgramacao();
        Assert.False(t.Sistema.Config.Atual.LiberarDevolucao);
        Assert.False(t.Sistema.Config.Atual.LiberarReimpressao);
    }
}
