using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Programar várias máquinas pelo pendrive, começar outra festa e deixar a máquina como nova.</summary>
public class ProgramacaoTests : IDisposable
{
    private readonly SistemaTemporario _a = new();
    private readonly SistemaTemporario _b = new();

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private static string Foto(Sistema s, string nome, SKColor cor)
    {
        var relativo = Path.Combine("imagens", "produtos", nome + ".png");
        var caminho = Path.Combine(s.PastaDados, relativo);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        using var bmp = new SKBitmap(64, 64);
        using (var c = new SKCanvas(bmp)) c.Clear(cor);
        ImagemUtil.SalvarPng(bmp, caminho);
        return relativo;
    }

    private static void Vender(Sistema s, int vezes)
    {
        var sessao = s.Caixa.Abrir(s.Config.Atual.NumeroCaixa, null, 0);
        var pastel = s.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        for (var i = 0; i < vezes; i++)
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(pastel);
            s.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 1000);
        }
        s.Caixa.Fechar(sessao, null);
    }

    /// <summary>Máquina A programada: evento, ficha, senha, foto, logo e um combo (texto livre + produto ligado).</summary>
    private void ProgramarA()
    {
        var a = _a.Sistema;
        var config = a.Config.Atual.Clonar();
        config.NomeEvento = "FESTA JUNINA";
        config.Modelo = ModeloFicha.Classico4;
        config.SenhaMaster = "2468";
        config.Colunas = 5;
        config.Logo = Foto(a, "logo", SKColors.Black);
        a.Config.Salvar(config);

        var pastel = a.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        pastel.Imagem = Foto(a, "pastel", SKColors.Orange);
        a.Catalogo.SalvarProduto(pastel, 30);
        var cerveja = a.Catalogo.Produtos().First(p => p.Nome == "CERVEJA");
        a.Catalogo.SalvarProduto(new Produto
        {
            Nome = "Combo cerveja", AbaId = cerveja.AbaId, Posicao = a.Catalogo.PosicoesLivres(cerveja.AbaId, 30).First(),
            PrecoCentavos = 3500,
            Componentes =
            [
                new ComponenteCombo { ProdutoId = cerveja.Id, Nome = "CERVEJA", Quantidade = 5, ValorCentavos = 800 },
                new ComponenteCombo { Nome = "Vale R$ 1,00", Detalhe = "val. 05/10", Quantidade = 2, ValorCentavos = 100 },
            ],
        }, 30);
        Vender(a, 3);
    }

    [Fact]
    public void Programacao_vai_pelo_pendrive_e_a_outra_maquina_so_muda_o_numero_do_caixa()
    {
        ProgramarA();
        var arquivo = Path.Combine(_a.Pasta, _a.Sistema.Programacao.NomeArquivo());
        Assert.EndsWith("BCFichas - FESTA JUNINA.bcf", arquivo);
        _a.Sistema.Programacao.Salvar(arquivo);

        var resumo = _a.Sistema.Programacao.Resumo(arquivo);
        Assert.Equal("FESTA JUNINA", resumo.Evento);
        Assert.Equal(16, resumo.Produtos);
        Assert.Equal(1, resumo.Combos);

        // Máquina B: outra impressora, outro número e vendas antigas
        var b = _b.Sistema;
        var configB = b.Config.Atual.Clonar();
        configB.NumeroCaixa = 7;
        configB.AjusteHorizontal = -8;
        configB.Zoom = 90;
        b.Config.Salvar(configB);
        Vender(b, 4);

        b.Programacao.Carregar(arquivo, 2);

        var c = b.Config.Atual;
        Assert.Equal("FESTA JUNINA", c.NomeEvento);
        Assert.Equal(ModeloFicha.Classico4, c.Modelo);
        Assert.Equal("2468", c.SenhaMaster);
        Assert.Equal(5, c.Colunas);
        Assert.Equal(2, c.NumeroCaixa);                       // o número escolhido
        Assert.Equal(-8, c.AjusteHorizontal);                 // impressora e tela continuam as da máquina B
        Assert.Equal(90, c.Zoom);
        Assert.Equal(_b.PastaImpressoes, c.PastaArquivo);
        Assert.True(File.Exists(b.Impressao.CaminhoImagem(c.Logo)));

        var produtos = b.Catalogo.Produtos();
        Assert.Equal(_a.Sistema.Catalogo.Produtos().Select(p => p.Nome).Order(), produtos.Select(p => p.Nome).Order());
        Assert.Equal(_a.Sistema.Catalogo.Abas().Select(a => a.Nome), b.Catalogo.Abas().Select(a => a.Nome));
        Assert.True(File.Exists(b.Impressao.CaminhoImagem(produtos.First(p => p.Nome == "PASTEL").Imagem)));
        var combo = produtos.Single(p => p.Nome == "COMBO CERVEJA");
        Assert.Equal(7, combo.FichasPorVenda);
        Assert.Equal(produtos.Single(p => p.Nome == "CERVEJA").Id, combo.Componentes[0].ProdutoId);
        Assert.Null(combo.Componentes[1].ProdutoId);
        Assert.Equal("VAL. 05/10", combo.Componentes[1].Detalhe);

        // As vendas antigas da máquina B saíram e a numeração recomeça
        Assert.Equal(0, b.Programacao.Situacao().Pedidos);
        var sessao = b.Caixa.Abrir(2, null, 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(b.Catalogo.Produto(combo.Id)!);
        var pedido = b.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Pix);
        Assert.Equal(1, pedido.Numero);
        Assert.Equal(7, GeradorFichas.Gerar(b.Vendas.Pedido(pedido.Id)!, b.Config.Atual).Count);

        // Ficou a cópia de segurança de antes de carregar
        Assert.Single(Directory.GetFiles(b.Programacao.PastaBackups, "*antes-de-carregar.db"));
    }

    [Fact]
    public void Comecar_outra_festa_apaga_as_vendas_e_mantem_os_produtos()
    {
        ProgramarA();
        var a = _a.Sistema;
        var produtos = a.Catalogo.Produtos().Count;
        Assert.Equal(3, a.Programacao.Situacao().Pedidos);

        a.Programacao.ApagarVendas();

        Assert.Equal(0, a.Programacao.Situacao().Pedidos);
        Assert.Equal(0, a.Programacao.Situacao().Caixas);
        Assert.Equal(produtos, a.Catalogo.Produtos().Count);
        Assert.Equal("FESTA JUNINA", a.Config.Atual.NomeEvento);
        Assert.Empty(a.Caixa.Sessoes(new DateTime(2000, 1, 1), DateTime.Today));
        Vender(a, 1);
        Assert.Equal(1, a.Vendas.PedidoPorNumero(1)!.Numero);
        // A cópia de segurança tem as vendas de antes
        var copia = Assert.Single(Directory.GetFiles(a.Programacao.PastaBackups, "*antes-de-apagar-vendas.db"));
        var antigo = new BCFichas.Core.Dados.Banco(copia);
        Assert.Equal(3, antigo.Escalar<long>("SELECT COUNT(*) FROM pedidos"));
    }

    [Fact]
    public void Como_nova_apaga_tudo_menos_impressora_numero_e_senha()
    {
        ProgramarA();
        var a = _a.Sistema;
        var config = a.Config.Atual.Clonar();
        config.NumeroCaixa = 4;
        config.AjusteHorizontal = 12;
        a.Config.Salvar(config);

        a.Programacao.DeixarComoNova();

        Assert.Empty(a.Catalogo.Produtos());
        Assert.Equal(["ITENS"], a.Catalogo.Abas().Select(x => x.Nome));
        Assert.Equal(0, a.Programacao.Situacao().Pedidos);
        var c = a.Config.Atual;
        Assert.Equal(new Configuracao().NomeEvento, c.NomeEvento);
        Assert.Equal(new Configuracao().Modelo, c.Modelo);
        Assert.Null(c.Logo);
        Assert.Equal(4, c.NumeroCaixa);
        Assert.Equal(12, c.AjusteHorizontal);
        Assert.Equal(TipoImpressora.Arquivo, c.Impressora);
        Assert.Equal("2468", c.SenhaMaster);
        Assert.Single(Directory.GetFiles(a.Programacao.PastaBackups, "*antes-de-deixar-como-nova.db"));
    }

    [Fact]
    public void Nao_mexe_com_caixa_aberto_e_recusa_arquivo_que_nao_e_programacao()
    {
        var a = _a.Sistema;
        a.Caixa.Abrir(1, null, 0);
        Assert.Equal("Feche o caixa antes (Menu → Fechar caixa).",
            Assert.Throws<ErroDeNegocio>(() => a.Programacao.ApagarVendas()).Message);
        Assert.Throws<ErroDeNegocio>(() => a.Programacao.DeixarComoNova());

        var falso = Path.Combine(_a.Pasta, "falso.bcf");
        File.WriteAllText(falso, "não sou um zip");
        Assert.Throws<ErroDeNegocio>(() => _b.Sistema.Programacao.Resumo(falso));
        Assert.Throws<ErroDeNegocio>(() => _b.Sistema.Programacao.Carregar(falso, 1));
        Assert.NotEmpty(_b.Sistema.Catalogo.Produtos()); // nada foi apagado
    }

    [Fact]
    public void Guarda_so_as_ultimas_copias_de_seguranca()
    {
        var p = _a.Sistema.Programacao;
        for (var i = 0; i < 18; i++) p.CopiaDeSeguranca("teste" + i.ToString("00"));
        Assert.Equal(15, Directory.GetFiles(p.PastaBackups, "*.db").Length);
    }
}
