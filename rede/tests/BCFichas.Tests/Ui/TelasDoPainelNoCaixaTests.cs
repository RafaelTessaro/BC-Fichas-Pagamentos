using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Tests.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// O painel no celular do lado do caixa: ligar na aba Máquina (PIN, endereços, QR code), o item do Menu com o QR
/// code e o PIN, e o painel indo junto no backup.
/// </summary>
public class TelasDoPainelNoCaixaTests
{
    static TelasDoPainelNoCaixaTests()
    {
        // O endereço que o roteador do evento daria para a máquina (o do ambiente de teste muda a cada vez)
        PainelDaRede.Enderecos = () => [IPAddress.Parse("192.168.1.10")];
        PainelDaRede.Respondendo = () => Task.FromResult(true);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("000000", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_tem_de_4_a_8_numeros(string? pin, bool valido) => Assert.Equal(valido, Configuracao.PinValido(pin));

    [Fact]
    public void Link_do_qr_code_leva_o_pin()
    {
        var ip = IPAddress.Parse("192.168.1.11");
        Assert.Equal("http://192.168.1.11:8765/?pin=2580", PainelDaRede.Link(ip, "2580"));
        Assert.Equal("http://192.168.1.11:8765/", PainelDaRede.Link(ip, ""));
    }

    [Fact]
    public void Backup_leva_o_painel_e_o_novo_evento_mantem()
    {
        using var a = new SistemaTemporario();
        using var b = new SistemaTemporario();
        var c = a.Sistema.Config.Atual.Clonar();
        c.PainelAtivo = true;
        c.PainelPin = "4321";
        c.PainelMaquinas = "192.168.1.10 192.168.1.11";
        a.Sistema.Config.Salvar(c);

        // Restaurar na outra máquina: o mesmo PIN e a mesma lista (o painel junta só as do mesmo PIN)
        var arquivo = Path.Combine(a.Pasta, a.Sistema.Programacao.NomeArquivo());
        a.Sistema.Programacao.Salvar(arquivo);
        b.Sistema.Programacao.Carregar(arquivo, 2);
        var restaurada = b.Sistema.Config.Atual;
        Assert.True(restaurada.PainelAtivo);
        Assert.Equal("4321", restaurada.PainelPin);
        Assert.Equal("192.168.1.10 192.168.1.11", restaurada.PainelMaquinas);

        // Novo evento: o painel é do kit (roteador e máquinas), continua ligado
        a.Sistema.Programacao.ZerarProgramacao();
        var zerada = a.Sistema.Config.Atual;
        Assert.True(zerada.PainelAtivo);
        Assert.Equal("4321", zerada.PainelPin);
        Assert.Equal("192.168.1.10 192.168.1.11", zerada.PainelMaquinas);
    }

    [AvaloniaFact]
    public async Task Ligar_o_painel_na_aba_maquina_sugere_pin_e_mostra_o_qr_code()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = ConfiguracaoViewModel.AbaMaquina;
        tela.SenhaTecnicaDigitada = TelaDeTeste.SenhaTecnica;
        await tela.LiberarMaquinaCommand.ExecuteAsync(null);
        Assert.True(tela.MaquinaLiberada);
        Assert.False(tela.PainelAtivo);
        Assert.Null(tela.PainelQr);

        // Ligou: já vem um PIN de 6 números e o QR code com o endereço desta máquina
        tela.PainelAtivo = true;
        Assert.True(Configuracao.PinValido(tela.PainelPin));
        Assert.Equal(6, tela.PainelPin.Length);
        Assert.NotNull(tela.PainelQr);
        Assert.Equal("http://192.168.1.10:8765", tela.PainelEndereco);
        Assert.True(tela.TemAlteracoes);

        // "Novo PIN" troca; PIN errado não grava
        var antes = tela.PainelPin;
        for (var i = 0; i < 5 && tela.PainelPin == antes; i++) tela.NovoPinCommand.Execute(null);
        Assert.NotEqual(antes, tela.PainelPin);
        tela.PainelPin = "12";
        Assert.Null(tela.PainelQr);
        tela.SalvarCommand.Execute(null);
        Assert.False(t.Sistema.Config.Atual.PainelAtivo);
        Assert.True(t.Principal.AvisoErro);
        Assert.Contains("PIN", t.Principal.Aviso);

        tela.PainelPin = "2580";
        tela.PainelMaquinas = " 192.168.1.10 192.168.1.11 192.168.1.12 192.168.1.14 ";
        Assert.NotNull(tela.PainelQr);
        tela.SalvarCommand.Execute(null);
        t.Principal.Aviso = null;
        TelaDeTeste.Atualizar();

        // Rola até o bloco do painel para a foto
        var bloco = t.Achar<TextBlock>(x => x.Text == "Painel no celular").FindAncestorOfType<Border>()!;
        var rolagem = bloco.FindAncestorOfType<ScrollViewer>()!;
        var topo = bloco.TranslatePoint(new Point(0, 0), (Visual)rolagem.Content!)!.Value.Y;
        rolagem.Offset = new Vector(0, topo - 8);
        TelaDeTeste.Atualizar();
        t.Foto("62-config-maquina-painel");

        var c = t.Sistema.Config.Atual;
        Assert.True(c.PainelAtivo);
        Assert.Equal("2580", c.PainelPin);
        Assert.Equal("192.168.1.10 192.168.1.11 192.168.1.12 192.168.1.14", c.PainelMaquinas);
        Assert.False(tela.TemAlteracoes);

        // Desligar não apaga o PIN (ao ligar de novo, continua o mesmo do evento)
        tela.PainelAtivo = false;
        tela.SalvarCommand.Execute(null);
        Assert.False(t.Sistema.Config.Atual.PainelAtivo);
        Assert.Equal("2580", t.Sistema.Config.Atual.PainelPin);
    }

    [AvaloniaFact]
    public void Menu_mostra_o_painel_no_celular_so_quando_ligado()
    {
        using (var desligado = new TelaDeTeste())
        {
            desligado.AbrirCaixa();
            desligado.Venda.AbrirMenuCommand.Execute(null);
            var menu = Assert.IsType<MenuViewModel>(desligado.Principal.Dialogo);
            Assert.DoesNotContain(menu.Itens, i => i.Titulo == "Painel no celular");
        }

        using var t = new TelaDeTeste(configurar: c =>
        {
            c.PainelAtivo = true;
            c.PainelPin = "2580";
        });
        t.AbrirCaixa();
        t.Venda.AbrirMenuCommand.Execute(null);
        var comPainel = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        var item = Assert.Single(comPainel.Itens, i => i.Titulo == "Painel no celular");
        Assert.Equal(comPainel.Itens.FindIndex(i => i.Titulo == "Relatórios") + 1, comPainel.Itens.IndexOf(item));
    }

    [AvaloniaFact]
    public async Task Painel_no_celular_pelo_menu_pede_a_senha_dos_relatorios_e_mostra_qr_e_pin()
    {
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.PainelAtivo = true;
            c.PainelPin = "2580";
            c.SenhaMaster = "1234";
            c.TelasProtegidas |= TelaProtegida.Relatorios;
        });
        t.AbrirCaixa();
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        var item = Assert.Single(menu.Itens, i => i.Titulo == "Painel no celular");
        Assert.True(item.Protegido);
        menu.EscolherCommand.Execute(item);

        // A mesma senha dos relatórios (o PIN abre as vendas no celular)
        var senha = Assert.IsType<SenhaViewModel>(t.Principal.Dialogo);
        foreach (var d in "1234") senha.TeclaCommand.Execute(d.ToString());
        senha.ConfirmarCommand.Execute(null);

        var painel = Assert.IsType<PainelCelularViewModel>(t.Principal.Dialogo);
        Assert.Equal("2580", painel.Pin);
        Assert.Equal("http://192.168.1.10:8765", painel.Endereco);
        Assert.NotNull(painel.Qr);
        Assert.False(painel.SemRede);
        await TelaDeTeste.Esperar(() => painel.Funcionando);
        t.Achar<TextBlock>(x => x.Text == "Painel funcionando nesta máquina");
        t.Foto("63-menu-painel-no-celular");

        painel.FecharCommand.Execute(null);
        Assert.Null(t.Principal.Dialogo);
    }

    [AvaloniaFact]
    public async Task Sem_rede_o_painel_avisa_para_conectar_no_roteador()
    {
        var antes = PainelDaRede.Enderecos;
        PainelDaRede.Enderecos = () => [];
        try
        {
            using var t = new TelaDeTeste(configurar: c =>
            {
                c.PainelAtivo = true;
                c.PainelPin = "2580";
            });
            var painel = new PainelCelularViewModel(t.Principal);
            t.Principal.AbrirDialogo(painel);
            Assert.True(painel.SemRede);
            Assert.Null(painel.Qr);
            await TelaDeTeste.Esperar(() => !painel.Conferindo);
            Assert.False(painel.Parado); // sem rede não acusa o painel
            t.Achar<TextBlock>(x => x.Text == "Este tablet não está na rede do roteador.");
        }
        finally
        {
            PainelDaRede.Enderecos = antes;
        }
    }
}
