using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

public class ImpressaoTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    [Theory]
    [InlineData(ModeloFicha.Completa, 80)]
    [InlineData(ModeloFicha.Compacta, 80)]
    [InlineData(ModeloFicha.Destaque, 80)]
    [InlineData(ModeloFicha.Completa, 58)]
    public void Ficha_tem_largura_da_impressora(ModeloFicha modelo, int papel)
    {
        var config = S.Config.Atual.Clonar();
        config.Modelo = modelo;
        config.LarguraPapelMm = papel;
        config.CodigoDeBarras = true;
        var ficha = RenderizadorFicha.Exemplo(config);

        using var bitmap = RenderizadorFicha.Renderizar(ficha, config, null);

        Assert.Equal(papel == 80 ? 576 : 384, bitmap.Width);
        Assert.InRange(bitmap.Height, 150, 900);
        var lum = ImagemUtil.Luminancia(bitmap);
        Assert.Contains(lum, v => v < ImagemUtil.Limiar);
        Assert.Contains(lum, v => v >= ImagemUtil.Limiar);
    }

    [Fact]
    public void Nome_grande_quebra_em_ate_duas_linhas_sem_estourar()
    {
        var config = S.Config.Atual.Clonar();
        var curta = RenderizadorFicha.Exemplo(config);
        var longa = new Ficha
        {
            NomeEvento = config.NomeEvento,
            Produto = "PORÇÃO DE BATATA FRITA COM CHEDDAR E BACON GIGANTE",
            NumeroPedido = 1, Caixa = 1, Data = DateTime.Now, Sequencia = 1, TotalFichas = 1,
        };
        using var a = RenderizadorFicha.Renderizar(curta, config, null);
        using var b = RenderizadorFicha.Renderizar(longa, config, null);
        Assert.Equal(576, b.Width);
        Assert.InRange(b.Height, a.Height - 120, a.Height + 120);
    }

    [Fact]
    public void Imagem_escpos_tem_cabecalho_gs_v_0_e_bits_certos()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var preto = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, 0, 1, 1, preto);   // primeiro ponto da linha 1
            canvas.DrawRect(15, 1, 1, 1, preto);  // último ponto da linha 2
        }

        var dados = EscPos.Imagem(bitmap);

        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 2, 0, 2, 0 }, dados[..8]);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00, 0x01 }, dados[8..]);
    }

    [Fact]
    public void Imagem_alta_e_dividida_em_blocos()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(576, 300, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White);
        var dados = EscPos.Imagem(bitmap);
        // 3 blocos (128 + 128 + 44 linhas), 8 bytes de cabeçalho cada, 72 bytes por linha.
        Assert.Equal(3 * 8 + 300 * 72, dados.Length);
    }

    [Fact]
    public void Trabalho_corta_cada_ficha()
    {
        using var a = new SKBitmap(8, 1);
        using var b = new SKBitmap(8, 1);
        a.Erase(SKColors.White);
        b.Erase(SKColors.White);
        var dados = EscPos.Trabalho([a, b], cortar: true);
        Assert.Equal(new byte[] { 0x1B, 0x40 }, dados[..2]);
        var cortes = Enumerable.Range(0, dados.Length - 3)
            .Count(i => dados[i] == 0x1D && dados[i + 1] == 0x56 && dados[i + 2] == 0x42);
        Assert.Equal(2, cortes);
    }

    [Fact]
    public void Venda_imprime_fichas_no_destino_arquivo()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(S.Catalogo.Produtos().First(p => p.Nome == "PASTEL"), 2);
        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 2000);

        var resultado = S.Impressao.Fichas(GeradorFichas.Gerar(pedido, S.Config.Atual));

        Assert.True(resultado.Ok, resultado.Mensagem);
        Assert.Equal(2, Directory.GetFiles(_t.PastaImpressoes, "*.png").Length);
    }

    [Fact]
    public void Fechamento_de_caixa_e_renderizado()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 1000);
        S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 500, "cofre");
        var resumo = S.Caixa.Fechar(sessao, 500);
        var resultado = S.Impressao.Documento(Relatorios.Fechamento(resumo, S.Config.Atual));
        Assert.True(resultado.Ok, resultado.Mensagem);
        var arquivo = Assert.Single(Directory.GetFiles(_t.PastaImpressoes, "*.png"));
        using var imagem = SKBitmap.Decode(arquivo);
        Assert.Equal(576, imagem.Width);
        Assert.True(imagem.Height > 600);
    }

    [Fact]
    public void Impressora_windows_fora_do_windows_da_erro_amigavel()
    {
        var config = S.Config.Atual.Clonar();
        config.Impressora = TipoImpressora.Windows;
        config.NomeImpressora = "ELGIN i9";
        S.Config.Salvar(config);
        var resultado = S.Impressao.Documento(new Documento().Linha("oi"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(resultado.Ok);
            Assert.Contains("Windows", resultado.Mensagem);
        }
    }

    [Theory]
    [InlineData("01000123001")]
    [InlineData("0100012300")]
    [InlineData("ABC-123")]
    public void Code128_tem_estrutura_valida(string texto)
    {
        var modulos = Code128.Modulos(texto);
        // Zona de silêncio dos dois lados e termina com a barra final do STOP.
        Assert.All(modulos[..10], Assert.False);
        Assert.All(modulos[^10..], Assert.False);
        Assert.True(modulos[^11]);
        Assert.True(modulos[10]);

        var simbolos = texto.Length % 2 == 0 && texto.All(char.IsAsciiDigit) ? texto.Length / 2 : texto.Length;
        // start + dados + checksum = 11 módulos cada; stop = 13.
        Assert.Equal(20 + (simbolos + 2) * 11 + 13, modulos.Length);
    }
}

public class AmostrasDeImpressao
{
    /// <summary>Gera as imagens de exemplo das fichas (pasta saida/fichas) para conferir o visual.</summary>
    [Fact]
    public void Gera_amostras_das_fichas_e_do_fechamento()
    {
        var pasta = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "saida", "fichas"));
        Directory.CreateDirectory(pasta);

        using var t = new SistemaTemporario();
        var config = t.Sistema.Config.Atual.Clonar();
        config.NomeEvento = "FESTA DE SÃO JOÃO";
        config.CodigoDeBarras = true;

        // Logo de exemplo: círculo com uma cruz, parecido com logos de paróquia.
        using var logo = new SKBitmap(200, 200);
        using (var c = new SKCanvas(logo))
        {
            c.Clear(SKColors.White);
            using var p = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 14, Style = SKPaintStyle.Stroke };
            c.DrawCircle(100, 100, 85, p);
            p.Style = SKPaintStyle.Fill;
            c.DrawRect(88, 35, 24, 130, p);
            c.DrawRect(50, 70, 100, 24, p);
        }
        var logoPng = Path.Combine(t.Pasta, "logo.png");
        ImagemUtil.SalvarPng(logo, logoPng);
        config.Logo = logoPng;

        foreach (var modelo in Enum.GetValues<ModeloFicha>())
        {
            config.Modelo = modelo;
            using var previa = t.Sistema.Impressao.Previa(config);
            ImagemUtil.SalvarPng(previa, Path.Combine(pasta, $"ficha-{modelo.ToString().ToLowerInvariant()}.png"));
        }

        var sessao = t.Sistema.Caixa.Abrir(1, "MARIA", 10000);
        var carrinho = new Carrinho();
        carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL"), 3);
        carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First(p => p.Nome == "CERVEJA"), 2);
        t.Sistema.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 5000);
        t.Sistema.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 2000, "cofre");
        var resumo = t.Sistema.Caixa.Fechar(sessao, 15300);
        using var fechamento = Relatorios.Fechamento(resumo, config).Renderizar(config.LarguraPontos);
        using var mono = ImagemUtil.Monocromatico(fechamento);
        ImagemUtil.SalvarPng(mono, Path.Combine(pasta, "fechamento-de-caixa.png"));

        // Código só com números e tamanho par usa o conjunto C do Code 128.
        var layout = new Layout(576, 16);
        layout.CodigoDeBarras("0100012300", 80, Fontes.Normal, 18);
        using var barras = layout.Renderizar();
        ImagemUtil.SalvarPng(barras, Path.Combine(pasta, "codigo-conjunto-c.png"));

        Assert.Equal(5, Directory.GetFiles(pasta, "*.png").Length);
    }
}
