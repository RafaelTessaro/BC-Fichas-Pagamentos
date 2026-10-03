using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Versão 3.9: valores rápidos na abertura, aviso de alterações não salvas, abas criadas em sequência, voltar para
/// o menu.
/// </summary>
public class TelasDaVersao39Tests
{
    private const int AbaBotoes = 2;

    private static ConfiguracaoViewModel AbrirConfiguracao(TelaDeTeste t, int aba = 0)
    {
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = aba;
        TelaDeTeste.Atualizar();
        return tela;
    }

    [AvaloniaFact]
    public void Abertura_tem_valores_rapidos_que_somam_ao_digitado()
    {
        using var t = new TelaDeTeste();
        var abertura = Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        abertura.Troco.TeclaCommand.Execute("5");
        abertura.Troco.TeclaCommand.Execute("0");
        abertura.Troco.TeclaCommand.Execute("0");
        abertura.Troco.TeclaCommand.Execute("0");
        abertura.Troco.SomarCommand.Execute("10000");
        abertura.Troco.SomarCommand.Execute("2000");
        abertura.Troco.SomarCommand.Execute("100");
        Assert.Equal("R$ 171,00", abertura.Troco.Texto);
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.Button>(),
            b => Equals(b.Content, "+ R$ 100") && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Nova_aba_vem_vazia_e_nao_apaga_o_nome_ja_digitado()
    {
        using var t = new TelaDeTeste();
        var tela = AbrirConfiguracao(t, AbaBotoes);
        Assert.False(tela.TemAlteracoes);

        tela.NovaAbaCommand.Execute(null);
        var salgados = tela.Abas.Last();
        Assert.Equal("", salgados.Nome);
        salgados.Nome = "Salgados";
        tela.NovaAbaCommand.Execute(null);
        tela.Abas.Last().Nome = "Porções";
        tela.NovaAbaCommand.Execute(null); // ficou sem nome: não é criada
        TelaDeTeste.Atualizar();
        Assert.Equal("Salgados", salgados.Nome); // o nome digitado continua lá
        Assert.True(tela.TemAlteracoes);
        t.Foto("50-config-abas-novas");

        // Trocar a ordem e excluir também só valem ao salvar
        tela.SubirAbaCommand.Execute(tela.Abas.Single(a => a.Nome == "Porções"));
        Assert.Equal(["COMIDAS", "BEBIDAS", "DOCES"], t.Sistema.Catalogo.Abas().Select(a => a.Nome));

        tela.SalvarCommand.Execute(null);
        Assert.Equal("Configurações salvas.", t.Principal.Aviso);
        Assert.Equal(["COMIDAS", "BEBIDAS", "DOCES", "PORÇÕES", "SALGADOS"], t.Sistema.Catalogo.Abas().Select(a => a.Nome));
        Assert.False(tela.TemAlteracoes);
        Assert.Equal(5, tela.Abas.Count);
    }

    [AvaloniaFact]
    public async Task Excluir_aba_vale_ao_salvar_e_nao_deixa_excluir_aba_com_produtos()
    {
        using var t = new TelaDeTeste();
        var tela = AbrirConfiguracao(t, AbaBotoes);
        tela.NovaAbaCommand.Execute(null);
        tela.Abas.Last().Nome = "Vazia";
        tela.SalvarCommand.Execute(null);

        // Aba com produtos: avisa e não tira da lista
        await tela.ExcluirAbaCommand.ExecuteAsync(tela.Abas.Single(a => a.Nome == "COMIDAS"));
        Assert.Equal("Esta aba ainda tem produtos. Mova ou exclua os produtos antes.", t.Principal.Aviso);
        Assert.Equal(4, tela.Abas.Count);

        var excluir = tela.ExcluirAbaCommand.ExecuteAsync(tela.Abas.Single(a => a.Nome == "VAZIA"));
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await excluir;
        Assert.Equal(3, tela.Abas.Count);
        Assert.Contains(t.Sistema.Catalogo.Abas(), a => a.Nome == "VAZIA"); // ainda não salvou
        Assert.True(tela.TemAlteracoes);
        tela.SalvarCommand.Execute(null);
        Assert.DoesNotContain(t.Sistema.Catalogo.Abas(), a => a.Nome == "VAZIA");
    }

    [AvaloniaFact]
    public async Task Sair_sem_salvar_pergunta_antes()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var tela = AbrirConfiguracao(t);
        Assert.False(tela.TemAlteracoes); // abrir não conta como mudança

        tela.NomeEvento = "Quermesse";
        TelaDeTeste.Atualizar();
        Assert.True(tela.TemAlteracoes);
        t.Foto("51-config-nao-salvo");

        // Voltar com mudança: pergunta; "Sair sem salvar" perde a mudança
        tela.VoltarCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        var pergunta = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Equal("Salvar as alterações?", pergunta.Titulo);
        Assert.Equal("Sair sem salvar", pergunta.TextoNao);
        pergunta.NaoCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Pagina is VendaViewModel);
        Assert.Equal("FESTA DE SÃO JOÃO", t.Sistema.Config.Atual.NomeEvento);

        // De novo, agora salvando
        tela = AbrirConfiguracao(t);
        tela.NomeEvento = "Quermesse";
        tela.VoltarCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Pagina is VendaViewModel);
        Assert.Equal("QUERMESSE", t.Sistema.Config.Atual.NomeEvento);

        // Sem mudança: sai direto
        tela = AbrirConfiguracao(t);
        tela.VoltarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.Null(t.Principal.Dialogo);
    }

    [AvaloniaFact]
    public async Task Desfazer_volta_ao_que_esta_salvo_e_senha_gravada_nao_conta_como_mudanca()
    {
        using var t = new TelaDeTeste();
        var tela = AbrirConfiguracao(t);
        tela.TelaCheia = true;
        Assert.True(tela.TemAlteracoes);
        var desfazer = tela.DesfazerCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await desfazer;
        Assert.False(tela.TelaCheia);
        Assert.False(tela.TemAlteracoes);

        // A senha é gravada na hora: não deixa o aviso de "não salvo"
        tela.DefinirSenhaCommand.Execute(null);
        var nova = Assert.IsType<NovaSenhaViewModel>(t.Principal.Dialogo);
        foreach (var senha in new[] { "1234", "1234" })
        {
            foreach (var d in senha) nova.TeclaCommand.Execute(d.ToString());
            nova.ConfirmarCommand.Execute(null);
        }
        Assert.Equal("1234", t.Sistema.Config.Atual.SenhaMaster);
        Assert.False(tela.TemAlteracoes);
    }

    [AvaloniaFact]
    public void Abas_ficam_na_barra_lateral_e_cabem_seis()
    {
        using var t = new TelaDeTeste();
        foreach (var nome in new[] { "Salgados", "Porções", "Sobremesas" })
            t.Sistema.Catalogo.SalvarAba(new BCFichas.Core.Aba { Nome = nome });
        t.AbrirCaixa();
        TelaDeTeste.Atualizar();
        var abas = t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("aba-lateral") && b.IsEffectivelyVisible).ToList();
        Assert.Equal(6, abas.Count);
        t.Venda.SelecionarAbaCommand.Execute(t.Venda.Abas[1]);
        TelaDeTeste.Atualizar();
        t.Foto("52-venda-abas-na-lateral");
        // Todas inteiras na tela de 1280 × 800, sem precisar rolar (o relógio vem logo embaixo)
        var fimDasAbas = abas.Max(b => b.TranslatePoint(new Point(0, b.Bounds.Height), t.Janela)!.Value.Y);
        var relogio = t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .First(x => x.Text == t.Principal.Relogio && x.IsEffectivelyVisible);
        Assert.True(fimDasAbas <= relogio.TranslatePoint(new Point(0, 0), t.Janela)!.Value.Y, $"abas até {fimDasAbas}");
    }

    [AvaloniaFact]
    public void Voltar_de_uma_tela_do_menu_mostra_o_menu_de_novo()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        menu.EscolherCommand.Execute(menu.Itens.Single(i => i.Titulo == "Relatórios"));
        Assert.IsType<RelatoriosViewModel>(t.Principal.Pagina);
        Assert.Null(t.Principal.Dialogo);

        ((PaginaViewModel)t.Principal.Pagina!).VoltarCommand.Execute(null);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo); // o menu continua aberto
        menu.FecharCommand.Execute(null);                          // só fecha no X
        Assert.Null(t.Principal.Dialogo);
    }
}
