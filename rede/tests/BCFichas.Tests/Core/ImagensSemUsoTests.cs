using BCFichas.Core;
using BCFichas.Core.Impressao;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// As fotos dos produtos e os logotipos ficam na pasta de dados: a mesma imagem não vira outra cópia e o que
/// ninguém usa mais é apagado (ao abrir o programa, ao restaurar, ao apagar as vendas e ao zerar a programação).
/// </summary>
public class ImagensSemUsoTests : IDisposable
{
    private readonly SistemaTemporario _a = new();
    private readonly SistemaTemporario _b = new();
    private Sistema S => _a.Sistema;

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private string Origem(string nome, SKColor cor)
    {
        var caminho = Path.Combine(_a.Pasta, nome + ".png");
        using var bmp = new SKBitmap(300, 300);
        using (var c = new SKCanvas(bmp)) c.Clear(cor);
        ImagemUtil.SalvarPng(bmp, caminho);
        return caminho;
    }

    private string Foto(SKColor cor) =>
        Path.Combine("imagens", "produtos",
            ImagemUtil.Importar(Origem("foto", cor), Path.Combine(S.PastaImagens, "produtos"), "", 256));

    private string Logo(SKColor cor) =>
        Path.Combine("imagens", ImagemUtil.Importar(Origem("logo", cor), S.PastaImagens, "logo-", 600));

    private string[] Arquivos() =>
        Directory.GetFiles(S.PastaImagens, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(S.PastaDados, f)).Order().ToArray();

    private void UsarFoto(string produto, string? foto)
    {
        var p = S.Catalogo.Produtos().First(x => x.Nome == produto);
        p.Imagem = foto;
        S.Catalogo.SalvarProduto(p, 30);
    }

    private void UsarLogo(string? logo)
    {
        var config = S.Config.Atual.Clonar();
        config.Logo = logo;
        S.Config.Salvar(config);
    }

    [Fact]
    public void A_mesma_imagem_escolhida_de_novo_nao_vira_outra_copia()
    {
        var primeira = Foto(SKColors.Orange);
        var deNovo = Foto(SKColors.Orange);
        var outra = Foto(SKColors.Green);
        Assert.Equal(primeira, deNovo);
        Assert.NotEqual(primeira, outra);
        Assert.Equal(Logo(SKColors.Black), Logo(SKColors.Black));
        Assert.StartsWith("logo-", Path.GetFileName(Logo(SKColors.Black)));
        Assert.Equal(3, Arquivos().Length); // 2 fotos e 1 logotipo
    }

    [Fact]
    public void Apaga_so_as_imagens_que_ninguem_usa()
    {
        var pastel = Foto(SKColors.Orange);
        var cerveja = Foto(SKColors.Yellow);
        var trocada = Foto(SKColors.Red);
        var logoVelho = Logo(SKColors.Gray);
        var logo = Logo(SKColors.Black);
        UsarFoto("PASTEL", pastel);
        UsarFoto("CERVEJA", cerveja);
        UsarFoto("BOLO", cerveja); // a mesma foto em dois produtos
        UsarLogo(logo);
        // Produto fora da tela de venda continua com a foto
        var cerveja2 = S.Catalogo.Produtos().First(x => x.Nome == "CERVEJA");
        cerveja2.Ativo = false;
        S.Catalogo.SalvarProduto(cerveja2, 30);

        Assert.Equal(2, S.Programacao.LimparImagensSemUso()); // a foto trocada e o logotipo velho
        Assert.Equal(new[] { pastel, cerveja, logo }.Order().ToArray(), Arquivos());
        Assert.False(File.Exists(Path.Combine(S.PastaDados, trocada)));
        Assert.False(File.Exists(Path.Combine(S.PastaDados, logoVelho)));

        // Produto excluído: a foto sai na próxima limpeza, ao abrir o programa de novo
        S.Catalogo.ExcluirProduto(S.Catalogo.Produtos().First(x => x.Nome == "PASTEL").Id);
        Sistema.Iniciar(_a.Pasta, criarExemplos: false);
        Assert.DoesNotContain(pastel, Arquivos());
        Assert.Contains(cerveja, Arquivos());
    }

    [Fact]
    public void Reprogramar_para_novo_evento_apaga_todas_as_fotos_os_logotipos_e_as_fichas_salvas()
    {
        UsarFoto("PASTEL", Foto(SKColors.Orange));
        UsarLogo(Logo(SKColors.Black));
        Logo(SKColors.Gray); // logotipo antigo que sobrou
        var impressoes = Path.Combine(S.PastaDados, ServicoImpressao.PastaArquivoPadrao);
        Directory.CreateDirectory(impressoes);
        File.WriteAllText(Path.Combine(impressoes, "ficha-1.png"), "x");

        S.Programacao.ZerarProgramacao();
        Assert.Empty(Arquivos());
        Assert.Empty(Directory.GetFiles(impressoes));
        Assert.Null(S.Config.Atual.Logo);
        // As cópias de segurança do banco ficam (são para desfazer um engano)
        Assert.NotEmpty(Directory.GetFiles(S.Programacao.PastaBackups));
    }

    [Fact]
    public void Restaurar_tira_as_imagens_da_programacao_anterior()
    {
        // Máquina B: programação do evento novo, com foto e logotipo
        var b = _b.Sistema;
        var origem = Path.Combine(_b.Pasta, "pipoca.png");
        using (var bmp = new SKBitmap(100, 100))
        {
            using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Purple);
            ImagemUtil.SalvarPng(bmp, origem);
        }
        var foto = Path.Combine("imagens", "produtos",
            ImagemUtil.Importar(origem, Path.Combine(b.PastaImagens, "produtos"), "", 256));
        var pipoca = b.Catalogo.Produtos().First(x => x.Nome == "PIPOCA");
        pipoca.Imagem = foto;
        b.Catalogo.SalvarProduto(pipoca, 30);
        var arquivo = Path.Combine(_b.Pasta, "evento-novo.bcf");
        b.Programacao.Salvar(arquivo);

        // Máquina A: fotos e logotipo do evento anterior
        var velha = Foto(SKColors.Orange);
        UsarFoto("PASTEL", velha);
        UsarLogo(Logo(SKColors.Black));

        S.Programacao.Carregar(arquivo, 3);
        Assert.Equal(new[] { foto }, Arquivos());
        Assert.Equal(foto, S.Catalogo.Produtos().First(x => x.Nome == "PIPOCA").Imagem);
    }
}
