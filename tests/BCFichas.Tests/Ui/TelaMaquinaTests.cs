using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Aba Máquina: backup da programação, restaurar, deixar pura para o cliente e como nova.</summary>
public class TelaMaquinaTests
{
    private const int AbaMaquina = 6;

    private static ConfiguracaoViewModel AbrirAbaMaquina(TelaDeTeste t)
    {
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = AbaMaquina;
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

        // O programador testou a máquina: caixa aberto com uma venda
        t.AbrirCaixa();
        Vender(t.Sistema, t.Principal.Sessao!, "PASTEL");

        var tela = AbrirAbaMaquina(t);
        Assert.StartsWith("Caixa 01 • 15 produto(s) em 3 aba(s)", tela.SituacaoMaquina);
        Assert.Equal("Guardado: 1 pedido(s) em 1 caixa(s) • caixa aberto", tela.SituacaoVendas);
        Assert.False(tela.MaquinaPura);
        tela.PastaBackup = pasta; // digitada no campo (vale na hora, sem tocar em Salvar)
        t.Foto("42-config-maquina");

        var backup = tela.FazerBackupCommand.ExecuteAsync(null);
        var feito = await EsperarMensagem(t);
        Assert.Equal("Backup feito", feito.Titulo);
        Assert.True(File.Exists(Path.Combine(pasta, "BCFichas - FESTA DE SÃO JOÃO.bcf")));
        Assert.True(File.Exists(Path.Combine(pendrive, "BCFichas - FESTA DE SÃO JOÃO.bcf")));
        Assert.Equal(pasta, t.Sistema.Config.Atual.PastaBackup);
        Assert.Contains("nunca as vendas", feito.Texto);
        Assert.Contains("Esta máquina ainda tem 1 pedido(s) em 1 caixa(s). Apagar agora", feito.Texto);
        Assert.Contains("Atenção: o caixa está aberto com 1 venda(s)", feito.Texto);
        t.Foto("46-backup-feito-deixar-pura");
        feito.SimCommand.Execute(null);
        await backup;

        Assert.True(t.Sistema.Programacao.Situacao().Pura);
        Assert.Null(t.Principal.Sessao);
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Equal("Vendas apagadas: a máquina está pura, só com a programação.", t.Principal.Aviso);

        tela = AbrirAbaMaquina(t);
        Assert.True(tela.MaquinaPura);
        Assert.Equal(pasta, tela.PastaBackup);
        t.Foto("48-config-maquina-pura");
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
        var config = t.Sistema.Config.Atual.Clonar();
        config.PastaBackup = pasta;
        t.Sistema.Config.Salvar(config);

        void Backup(string evento, string onde)
        {
            var c = t.Sistema.Config.Atual.Clonar();
            c.NomeEvento = evento;
            t.Sistema.Config.Salvar(c);
            t.Sistema.Programacao.Salvar(Path.Combine(onde, t.Sistema.Programacao.NomeArquivo()));
            Thread.Sleep(20);
        }

        // Um backup só (copiado pelo acesso remoto para a pasta): vai direto para o resumo
        Backup("QUERMESSE DO BAIRRO", pasta);
        var tela = AbrirAbaMaquina(t);
        var restaurar = tela.RestaurarCommand.ExecuteAsync(null);
        var resumo = await EsperarMensagem(t);
        Assert.Equal("Restaurar este backup?", resumo.Titulo);
        Assert.Contains("Evento: QUERMESSE DO BAIRRO", resumo.Texto);
        resumo.NaoCommand.Execute(null);
        await restaurar;

        // Dois backups (pasta e pendrive): escolhe na lista
        Backup("FESTA DO PENDRIVE", pendrive);
        restaurar = tela.RestaurarCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is EscolherBackupViewModel);
        var lista = (EscolherBackupViewModel)t.Principal.Dialogo!;
        Assert.Equal(["FESTA DO PENDRIVE", "QUERMESSE DO BAIRRO"], lista.Itens.Select(i => i.Evento));
        Assert.Equal(["Pendrive " + pendrive, "Pasta do backup"], lista.Itens.Select(i => i.Lugar));
        Assert.Contains("15 produto(s)", lista.Itens[0].Detalhes);
        t.Foto("47-restaurar-lista");
        lista.EscolherCommand.Execute(lista.Itens[1]);

        resumo = await EsperarMensagem(t);
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
        Assert.Equal(pasta, t.Sistema.Config.Atual.PastaBackup);
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
    public async Task Deixar_como_nova_volta_para_a_abertura_sem_produtos()
    {
        using var t = new TelaDeTeste();
        var tela = AbrirAbaMaquina(t);
        var apagar = tela.DeixarComoNovaCommand.ExecuteAsync(null);
        await EsperarMensagem(t);
        t.Foto("45-como-nova-confirmar");
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await apagar;

        Assert.Empty(t.Sistema.Catalogo.Produtos());
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Equal("Máquina como nova. Cadastre os produtos do próximo evento.", t.Principal.Aviso);
    }
}
