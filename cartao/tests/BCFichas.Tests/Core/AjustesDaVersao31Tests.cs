using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Servicos;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Caixa sem operador, mensagem fixa da BC Fichas separada do rodapé e faixa do modelo 7.</summary>
public class AjustesDaVersao31Tests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Caixa_abre_so_com_o_numero_e_o_troco()
    {
        var sessao = S.Caixa.Abrir(3, null, 5000);
        Assert.Equal("", sessao.Operador);
        Assert.Equal(5000, sessao.ValorAberturaCentavos);
        // Continua sem abrir o mesmo caixa duas vezes
        var erro = Assert.Throws<ErroDeNegocio>(() => S.Caixa.Abrir(3, "", 0));
        Assert.Equal("O caixa 03 já está aberto.", erro.Message);
    }

    [Fact]
    public void Rodape_antigo_igual_a_mensagem_fixa_fica_vazio_para_nao_sair_duas_vezes()
    {
        Assert.Equal("", new Configuracao().Rodape);
        // Configuração gravada pela versão 3.0, com o telefone como rodapé
        S.Banco.Executar("UPDATE config SET valor = $v WHERE chave = 'geral'",
            ("$v", """{"NomeEvento":"QUERMESSE","Rodape":"BC-FICHAS FONE: (19) 3023-9050"}"""));
        var lida = new ConfigServico(S.Banco).Atual;
        Assert.Equal("QUERMESSE", lida.NomeEvento);
        Assert.Equal("", lida.Rodape);

        // Rodapé do cliente continua; digitar a mensagem fixa no rodapé não a duplica
        var config = S.Config.Atual.Clonar();
        config.Rodape = "  OBRIGADO E VOLTE SEMPRE  ";
        S.Config.Salvar(config);
        Assert.Equal("OBRIGADO E VOLTE SEMPRE", S.Config.Atual.Rodape);
        config.Rodape = "bc-fichas fone: (19) 3023-9050";
        S.Config.Salvar(config);
        Assert.Equal("", S.Config.Atual.Rodape);
    }

    [Theory]
    [InlineData(ModeloFicha.Classico1)]
    [InlineData(ModeloFicha.Classico2)]
    [InlineData(ModeloFicha.Classico3)]
    [InlineData(ModeloFicha.Classico4)]
    [InlineData(ModeloFicha.Completa)]
    [InlineData(ModeloFicha.Compacta)]
    [InlineData(ModeloFicha.Destaque)]
    public void Toda_ficha_termina_com_a_mensagem_fixa_e_o_rodape_tem_espaco_proprio(ModeloFicha modelo)
    {
        var config = S.Config.Atual.Clonar();
        config.Modelo = modelo;
        config.Rodape = "";
        using var sem = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null);
        config.Rodape = "OBRIGADO E VOLTE SEMPRE";
        using var com = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null);

        // Sem rodapé ainda sai a linha da BC Fichas; com rodapé, ela continua e a ficha cresce uma linha
        Assert.True(TemTinta(sem, sem.Height - 40, sem.Height - 12), "a mensagem fixa não saiu no fim da ficha");
        Assert.InRange(com.Height - sem.Height, 20, 50);
    }

    [Fact]
    public void Rodape_igual_a_mensagem_fixa_nao_sai_duas_vezes_nem_na_previa()
    {
        var config = S.Config.Atual.Clonar();
        config.Rodape = "";
        using var sem = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null);
        // A prévia e o "Imprimir teste" montam a configuração da tela sem passar pelo Salvar
        config.Rodape = "BC-FICHAS FONE: (19) 3023-9050";
        using var repetido = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null);
        Assert.Equal(sem.Height, repetido.Height);
        Assert.Equal("", Configuracao.RodapeSemMensagemFixa("  Bc-Fichas Fone: (19) 3023-9050 "));
        Assert.Equal("VOLTE SEMPRE", Configuracao.RodapeSemMensagemFixa(" VOLTE SEMPRE "));
    }

    [Fact]
    public void Faixa_do_modelo_7_fica_dentro_da_moldura()
    {
        var config = S.Config.Atual.Clonar();
        config.Modelo = ModeloFicha.Destaque;
        config.Moldura = true;
        using var ficha = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null);
        Directory.CreateDirectory(Pasta);
        using (var arquivo = File.Create(Path.Combine(Pasta, "ficha-destaque-moldura.png")))
            ficha.Encode(arquivo, SKEncodedImageFormat.Png, 100);

        var meioDaFaixa = 30;
        // Fora da moldura (beirada do papel) fica branco; dentro, a faixa é preta e encosta na borda
        Assert.True(Branco(ficha.GetPixel(1, meioDaFaixa)), "a faixa passa para fora da moldura");
        Assert.True(Branco(ficha.GetPixel(ficha.Width - 2, meioDaFaixa)), "a faixa passa para fora da moldura");
        Assert.False(Branco(ficha.GetPixel(8, meioDaFaixa)));
        Assert.False(Branco(ficha.GetPixel(ficha.Width - 9, meioDaFaixa)));
        Assert.False(Branco(ficha.GetPixel(ficha.Width / 2, 8)), "a faixa deveria encostar na borda de cima");

        // Sem moldura, a faixa vai de lado a lado do papel, como antes
        config.Moldura = false;
        using var semMoldura = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null);
        Assert.False(Branco(semMoldura.GetPixel(1, 20)));
    }

    private static string Pasta => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "saida", "fichas"));

    private static bool Branco(SKColor c) => c.Red > 200 && c.Green > 200 && c.Blue > 200;

    private static bool TemTinta(SKBitmap b, int de, int ate)
    {
        for (var y = Math.Max(0, de); y < Math.Min(b.Height, ate); y++)
        for (var x = 20; x < b.Width - 20; x++)
            if (!Branco(b.GetPixel(x, y))) return true;
        return false;
    }
}
