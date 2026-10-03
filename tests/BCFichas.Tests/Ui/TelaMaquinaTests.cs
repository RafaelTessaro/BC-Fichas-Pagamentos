using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Aba Máquina: backup da programação, restaurar, deixar pura para o cliente e reprogramação para novo evento.</summary>
public class TelaMaquinaTests
{
    private const int AbaMaquina = 6;

    private static ConfiguracaoViewModel AbrirAbaMaquina(TelaDeTeste t)
    {
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = AbaMaquina;
        tela.MaquinaLiberada = true; // a senha técnica tem teste próprio (TelasDaVersao311Tests)
        TelaDeTeste.Atualizar();
        return tela;
    }

    private static void Vender(Sistema s, SessaoCaixa sessao, string produto)
    {
        var carrinho = new Carrinho();
        carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == produto));
        s.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 100_000);
    }

    private static async Task<MensagemViewModel> EsperarMensagem(TelaDeTeste t)
    {
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        return (MensagemViewModel)t.Principal.Dialogo!;
    }

    [AvaloniaFact]
    public async Task Faz_o_backup_na_pasta_e_no_pendrive_e_oferece_deixar_a_maquina_pura()
    {
        using var t = new TelaDeTeste();
        var pasta = Path.Combine(t.Sistema.PastaDados, "backup");
        var pendrive = Path.Combine(t.Sistema.PastaDados, "pendrive");
        Directory.CreateDirectory(pendrive);
        t.Principal.Pendrives = () => [pendrive];

        // O programador testou a máquina: vendeu e fechou o caixa
        var sessao = t.Sistema.Caixa.Abrir(1, null, 0);
        Vender(t.Sistema, sessao, "PASTEL");
        t.Sistema.Caixa.Fechar(sessao, null);

        var tela = AbrirAbaMaquina(t);
        Assert.StartsWith("Caixa 01 • 15 produto(s) em 3 aba(s)", tela.SituacaoMaquina);
        Assert.Equal("Guardado: 1 pedido(s) em 1 caixa(s)", tela.SituacaoVendas);
        Assert.False(tela.MaquinaPura);
        tela.PastaBackup = pasta; // digitada no campo (vale na hora, sem tocar em Salvar)
        t.Foto("42-config-maquina");

        var backup = tela.FazerBackupCommand.ExecuteAsync(null);
        var feito = await EsperarMensagem(t);
        Assert.Equal("Apagar as vendas desta máquina?", feito.Titulo);
        Assert.True(File.Exists(Path.Combine(pasta, "BCFichas - FESTA DE SÃO JOÃO.bcf")));
        Assert.True(File.Exists(Path.Combine(pendrive, "BCFichas - FESTA DE SÃO JOÃO.bcf")));
        Assert.Equal(pasta, t.Sistema.Config.Atual.PastaBackup);
        Assert.StartsWith("Backup feito. Salvo em:", feito.Texto);
        Assert.Contains("e no pendrive " + pendrive, feito.Texto);
        Assert.Contains("nunca as vendas", feito.Texto);
        Assert.Contains("Esta máquina ainda tem 1 pedido(s) em 1 caixa(s). Apagar agora", feito.Texto);
        t.Foto("46-backup-feito-deixar-pura");
        feito.SimCommand.Execute(null);
        await backup;

        Assert.True(t.Sistema.Programacao.Situacao().Pura);
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Equal("Vendas apagadas: a máquina está pura, só com a programação.", t.Principal.Aviso);

        tela = AbrirAbaMaquina(t);
        Assert.True(tela.MaquinaPura);
        Assert.Equal(pasta, tela.PastaBackup);
        t.Foto("48-config-maquina-pura");
    }

    [AvaloniaFact]
    public async Task Com_o_caixa_aberto_com_vendas_o_backup_nao_oferece_apagar()
    {
        using var t = new TelaDeTeste();
        t.Principal.Pendrives = () => [];
        t.AbrirCaixa();
        Vender(t.Sistema, t.Principal.Sessao!, "PASTEL");

        var tela = AbrirAbaMaquina(t);
        Assert.Equal("Guardado: 1 pedido(s) em 1 caixa(s) • caixa aberto", tela.SituacaoVendas);
        tela.PastaBackup = Path.Combine(t.Sistema.PastaDados, "backup");
        var backup = tela.FazerBackupCommand.ExecuteAsync(null);
        var feito = await EsperarMensagem(t);
        Assert.Equal("Backup feito", feito.Titulo);
        Assert.False(feito.TemNao); // só OK: nada de apagar com um toque no meio do evento
        Assert.Contains("caixa aberto com 1 venda(s)", feito.Texto);
        feito.SimCommand.Execute(null);
        await backup;
        Assert.NotNull(t.Principal.Sessao);
        Assert.Equal(1, t.Sistema.Programacao.Situacao().Pedidos);

        // Pelo botão da tela dá para apagar, com o aviso do caixa aberto
        var apagar = tela.LimparVendasCommand.ExecuteAsync(null);
        var confirmar = await EsperarMensagem(t);
        Assert.Contains("Atenção: o caixa está aberto com 1 venda(s) e será apagado sem imprimir o fechamento.", confirmar.Texto);
        confirmar.SimCommand.Execute(null);
        await apagar;
        Assert.Null(t.Principal.Sessao);
        Assert.True(t.Sistema.Programacao.Situacao().Pura);
    }

    [AvaloniaFact]
    public async Task Pergunta_se_salva_o_que_mudou_na_tela_antes_do_backup()
    {
        using var t = new TelaDeTeste();
        t.Principal.Pendrives = () => [];
        var pasta = Path.Combine(t.Sistema.PastaDados, "backup");
        var tela = AbrirAbaMaquina(t);
        tela.PastaBackup = pasta;
        tela.NomeEvento = "Quermesse nova"; // mudou o evento e não tocou em Salvar

        var backup = tela.FazerBackupCommand.ExecuteAsync(null);
        var salvar = await EsperarMensagem(t);
        Assert.Equal("Alterações não salvas", salvar.Titulo);
        salvar.SimCommand.Execute(null);
        var feito = await EsperarMensagem(t);
        Assert.Equal("Backup feito", feito.Titulo);
        feito.SimCommand.Execute(null);
        await backup;

        Assert.Equal("QUERMESSE NOVA", t.Sistema.Config.Atual.NomeEvento);
        var arquivo = Path.Combine(pasta, "BCFichas - QUERMESSE NOVA.bcf");
        Assert.Equal("QUERMESSE NOVA", t.Sistema.Programacao.Resumo(arquivo).Evento);

        // A maquininha também vai no backup; a impressora e a tela são da máquina e não contam
        tela.Zoom = 125;
        tela.TipoMaquininha = tela.TiposMaquininha.Single(m => m.Valor == TipoMaquininha.Simulador);
        backup = tela.FazerBackupCommand.ExecuteAsync(null);
        salvar = await EsperarMensagem(t);
        Assert.Equal("Alterações não salvas", salvar.Titulo);
        salvar.NaoCommand.Execute(null);
        await backup;
        tela.TipoMaquininha = tela.TiposMaquininha.Single(m => m.Valor == TipoMaquininha.Separada);
        backup = tela.FazerBackupCommand.ExecuteAsync(null);
        feito = await EsperarMensagem(t);
        Assert.Equal("Backup feito", feito.Titulo);
        feito.SimCommand.Execute(null);
        await backup;
    }

    [AvaloniaFact]
    public async Task Restaura_escolhendo_na_lista_e_pergunta_so_o_numero_do_caixa()
    {
        using var t = new TelaDeTeste();
        var pasta = Path.Combine(t.Sistema.PastaDados, "backup");
        var pendrive = Path.Combine(t.Sistema.PastaDados, "pendrive");
        Directory.CreateDirectory(pasta);
        Directory.CreateDirectory(pendrive);
        t.Principal.Pendrives = () => [pendrive];
        // A pasta que vai dentro dos backups é outra: a da máquina tem que continuar a dela
        var config = t.Sistema.Config.Atual.Clonar();
        config.PastaBackup = @"Z:\pasta de outra maquina";
        t.Sistema.Config.Salvar(config);

        void Backup(string evento, string onde)
        {
            var c = t.Sistema.Config.Atual.Clonar();
            c.NomeEvento = evento;
            t.Sistema.Config.Salvar(c);
            t.Sistema.Programacao.Salvar(Path.Combine(onde, t.Sistema.Programacao.NomeArquivo()));
            Thread.Sleep(20);
        }

        // Um backup só (copiado pelo acesso remoto para a pasta): aparece sozinho na lista
        Backup("QUERMESSE DO BAIRRO", pasta);
        var tela = AbrirAbaMaquina(t);
        tela.PastaBackup = pasta;
        var restaurar = tela.RestaurarCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is EscolherBackupViewModel);
        var sozinho = (EscolherBackupViewModel)t.Principal.Dialogo!;
        Assert.Equal("QUERMESSE DO BAIRRO", Assert.Single(sozinho.Itens).Evento);
        Assert.True(sozinho.PodeProcurar);
        sozinho.CancelarCommand.Execute(null);
        await restaurar;
        Assert.Null(t.Principal.Dialogo);

        // Dois backups (pasta e pendrive): escolhe na lista
        Backup("FESTA DO PENDRIVE", pendrive);
        restaurar = tela.RestaurarCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is EscolherBackupViewModel);
        var lista = (EscolherBackupViewModel)t.Principal.Dialogo!;
        Assert.Equal(["FESTA DO PENDRIVE", "QUERMESSE DO BAIRRO"], lista.Itens.Select(i => i.Evento));
        Assert.False(lista.TemRecusados);
        Assert.Equal(["Pendrive " + pendrive, "Pasta do backup"], lista.Itens.Select(i => i.Lugar));
        Assert.Contains("15 produto(s)", lista.Itens[0].Detalhes);
        t.Foto("47-restaurar-lista");
        lista.EscolherCommand.Execute(lista.Itens[1]);

        var resumo = await EsperarMensagem(t);
        Assert.Contains("Evento: QUERMESSE DO BAIRRO", resumo.Texto);
        Assert.Contains("15 produto(s) em 3 aba(s)", resumo.Texto);
        t.Foto("43-restaurar-resumo");
        resumo.SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is NumeroViewModel);
        var numero = (NumeroViewModel)t.Principal.Dialogo!;
        Assert.Equal("Número deste caixa (PDV)", numero.Titulo);
        Assert.Equal("1", numero.Digitado);
        numero.TeclaCommand.Execute("C");
        numero.TeclaCommand.Execute("3");
        t.Foto("44-numero-do-caixa");
        numero.ConfirmarCommand.Execute(null);
        await restaurar;

        Assert.Equal(3, t.Sistema.Config.Atual.NumeroCaixa);
        Assert.Equal("QUERMESSE DO BAIRRO", t.Sistema.Config.Atual.NomeEvento);
        Assert.Equal(pasta, t.Sistema.Config.Atual.PastaBackup); // a pasta continua a desta máquina
        Assert.Equal("Backup restaurado: QUERMESSE DO BAIRRO, Caixa 03.", t.Principal.Aviso);
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        // A configuração nova chega no cabeçalho pela fila da tela.
        await TelaDeTeste.Esperar(() => t.Principal.CaixaTexto == "CAIXA 03");
    }

    [AvaloniaFact]
    public async Task Sem_backup_diz_onde_por_o_arquivo_e_deixa_procurar_em_outro_lugar()
    {
        using var t = new TelaDeTeste();
        var pasta = Path.Combine(t.Sistema.PastaDados, "backup vazio");
        Directory.CreateDirectory(pasta);
        t.Principal.Pendrives = () => [];
        var outro = Path.Combine(t.Sistema.PastaDados, "Area de Trabalho", "festa.bcf");
        Directory.CreateDirectory(Path.GetDirectoryName(outro)!);
        t.Sistema.Programacao.Salvar(outro);
        string? abriuEm = "";
        t.Principal.EscolherProgramacao = inicio =>
        {
            abriuEm = inicio;
            return Task.FromResult<string?>(outro);
        };

        var tela = AbrirAbaMaquina(t);
        tela.PastaBackup = pasta;
        var restaurar = tela.RestaurarCommand.ExecuteAsync(null);
        var nenhum = await EsperarMensagem(t);
        Assert.Equal("Nenhum backup encontrado", nenhum.Titulo);
        Assert.Contains("Pasta do backup: " + pasta, nenhum.Texto);
        nenhum.SimCommand.Execute(null); // Procurar em outro lugar

        var resumo = await EsperarMensagem(t);
        Assert.Equal(pasta, abriuEm);
        Assert.Equal("Restaurar este backup?", resumo.Titulo);
        Assert.Contains(outro, resumo.Texto);
        resumo.NaoCommand.Execute(null);
        await restaurar;

        // Arquivos pela metade (cópia do acesso remoto que não terminou): diz o motivo em vez de "não achei", e
        // mostra só os 3 primeiros para a mensagem caber na tela
        for (var i = 1; i <= 5; i++) File.WriteAllText(Path.Combine(pasta, $"BCFichas - FESTA {i}.bcf"), "PK pela metade");
        restaurar = tela.RestaurarCommand.ExecuteAsync(null);
        var recusado = await EsperarMensagem(t);
        Assert.Equal("Não dá para usar o backup", recusado.Titulo);
        Assert.Contains("BCFichas - FESTA 1.bcf (Pasta do backup):", recusado.Texto);
        Assert.Contains("ainda está sendo copiado", recusado.Texto);
        Assert.EndsWith("... e mais 2 arquivo(s) que não dá para usar.", recusado.Texto);
        recusado.NaoCommand.Execute(null);
        await restaurar;
    }

    [AvaloniaFact]
    public async Task Apagar_as_vendas_pergunta_o_que_fazer_com_o_estoque()
    {
        using var t = new TelaDeTeste();
        var cerveja = t.Sistema.Catalogo.Produtos().First(p => p.Nome == "CERVEJA");
        cerveja.ControlaEstoque = true;
        cerveja.Estoque = 100;
        t.Sistema.Catalogo.SalvarProduto(cerveja, 30);
        var sessao = t.Sistema.Caixa.Abrir(1, null, 0);
        Vender(t.Sistema, sessao, "CERVEJA");
        Vender(t.Sistema, sessao, "CERVEJA");
        t.Sistema.Caixa.Fechar(sessao, null);

        var tela = AbrirAbaMaquina(t);
        var apagar = tela.LimparVendasCommand.ExecuteAsync(null);
        (await EsperarMensagem(t)).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel { Titulo: "E o estoque?" });
        var estoque = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Contains("As vendas tiraram 2 unidade(s) do estoque.", estoque.Texto);
        t.Foto("49-apagar-vendas-estoque");
        estoque.NaoCommand.Execute(null); // outra festa com o que sobrou
        await apagar;

        Assert.True(t.Sistema.Programacao.Situacao().Pura);
        Assert.Equal(98, t.Sistema.Catalogo.Produto(cerveja.Id)!.Estoque);
    }

    [AvaloniaFact]
    public async Task Apagar_as_vendas_desliga_o_modo_teste_e_volta_para_a_abertura()
    {
        using var t = new TelaDeTeste();
        var sessao = t.Sistema.Caixa.Abrir(1, null, 0, teste: true);
        Vender(t.Sistema, sessao, "PASTEL");
        Vender(t.Sistema, sessao, "CERVEJA");
        t.Principal.Iniciar();
        Assert.True(t.Principal.ModoTeste);
        t.Venda.LimparPedido();
        t.Tocar("PASTEL");

        var tela = AbrirAbaMaquina(t);
        Assert.Equal("Guardado: 2 pedido(s) de teste em 1 caixa(s) • modo teste ligado", tela.SituacaoVendas);
        var apagar = tela.LimparVendasCommand.ExecuteAsync(null);
        var confirmar = await EsperarMensagem(t);
        Assert.Equal("Apagar as vendas?", confirmar.Titulo);
        Assert.Contains("Apaga desta máquina 2 pedido(s) de teste em 1 caixa(s)", confirmar.Texto);
        Assert.Contains("O modo teste é desligado.", confirmar.Texto);
        confirmar.SimCommand.Execute(null);
        await apagar;

        Assert.False(t.Principal.ModoTeste);
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.True(t.Sistema.Programacao.Situacao().Pura);
        Assert.Empty(t.Principal.Venda.Linhas); // o pedido que estava sendo montado também sai

        // Já pura: só avisa
        tela = AbrirAbaMaquina(t);
        apagar = tela.LimparVendasCommand.ExecuteAsync(null);
        var pura = await EsperarMensagem(t);
        Assert.Equal("A máquina já está pura", pura.Titulo);
        pura.SimCommand.Execute(null);
        await apagar;
    }

    [AvaloniaFact]
    public async Task Reprogramacao_para_novo_evento_volta_para_a_abertura_sem_produtos()
    {
        using var t = new TelaDeTeste();
        var tela = AbrirAbaMaquina(t);
        var apagar = tela.ZerarProgramacaoCommand.ExecuteAsync(null);
        await EsperarMensagem(t);
        t.Foto("45-reprogramacao-confirmar");
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await apagar;

        Assert.Empty(t.Sistema.Catalogo.Produtos());
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Equal("Programação zerada. Cadastre os produtos do novo evento.", t.Principal.Aviso);
    }
}
