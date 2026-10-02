using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Backup da programação para outras máquinas, deixar a máquina pura e deixar a máquina como nova.</summary>
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
        configB.PastaBackup = @"D:\backups da B";
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
        Assert.Equal(@"D:\backups da B", c.PastaBackup);           // a pasta do backup também é da máquina
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
    public void Apagar_as_vendas_deixa_a_maquina_pura_e_mantem_os_produtos()
    {
        ProgramarA();
        var a = _a.Sistema;
        var produtos = a.Catalogo.Produtos().Count;
        Assert.Equal(3, a.Programacao.Situacao().Pedidos);
        Assert.False(a.Programacao.Situacao().Pura);

        a.Programacao.ApagarVendas();

        Assert.True(a.Programacao.Situacao().Pura);
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
    public void Apaga_ate_o_caixa_aberto_e_os_testes_e_o_estoque_volta_ao_de_antes_das_vendas()
    {
        var a = _a.Sistema;
        Produto Controlar(string nome, int estoque)
        {
            var p = a.Catalogo.Produtos().First(x => x.Nome == nome);
            p.ControlaEstoque = true;
            p.Estoque = estoque;
            a.Catalogo.SalvarProduto(p, 30);
            return p;
        }
        var pastel = Controlar("PASTEL", 50);
        var cerveja = Controlar("CERVEJA", 100);
        a.Catalogo.SalvarProduto(new Produto
        {
            Nome = "Combo cerveja", AbaId = cerveja.AbaId, Posicao = a.Catalogo.PosicoesLivres(cerveja.AbaId, 30).First(),
            PrecoCentavos = 3500, ControlaEstoque = true, Estoque = 10,
            Componentes = [new ComponenteCombo { ProdutoId = cerveja.Id, Nome = "CERVEJA", Quantidade = 5, ValorCentavos = 800 }],
        }, 30);
        var combo = a.Catalogo.Produtos().First(x => x.Nome == "COMBO CERVEJA");
        int Estoque(Produto p) => a.Catalogo.Produto(p.Id)!.Estoque;

        Pedido Vender(SessaoCaixa sessao, Produto produto, int quantidade, FormaPagamento forma = FormaPagamento.Dinheiro)
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(a.Catalogo.Produto(produto.Id)!, quantidade);
            return a.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, 100_000);
        }

        // Caixa aberto com vendas, devoluções e um pagamento no cartão que não terminou
        var sessao = a.Caixa.Abrir(1, null, 0);
        var pasteis = Vender(sessao, pastel, 3);
        var combos = Vender(sessao, combo, 2);
        Vender(sessao, pastel, 1, FormaPagamento.Credito); // aguardando a maquininha: não baixou o estoque
        a.Devolucoes.Devolver(sessao, pasteis.Id, new Dictionary<long, int> { [pasteis.Itens[0].Id] = 1 }, null);
        var itemCombo = combos.Itens[0];
        a.Devolucoes.Devolver(sessao, combos.Id, [(itemCombo.Id, itemCombo.Componentes[0].Id, 2)], null);
        Assert.Equal(48, Estoque(pastel));
        Assert.Equal(8, Estoque(combo));
        Assert.Equal(92, Estoque(cerveja));

        // Caixa de teste aberto (o programador testando a máquina)
        var teste = a.Caixa.Abrir(1, null, 0, teste: true);
        Vender(teste, pastel, 2);

        var situacao = a.Programacao.Situacao();
        Assert.Equal(3, situacao.Pedidos);
        Assert.Equal(1, situacao.PedidosTeste);
        Assert.Equal(2, situacao.Caixas);
        Assert.True(situacao.CaixaAberto);
        Assert.Equal(3, situacao.PedidosNoCaixaAberto);
        Assert.True(situacao.ModoTeste);
        Assert.True(situacao.ControlaEstoque);

        a.Programacao.ApagarVendas();

        situacao = a.Programacao.Situacao();
        Assert.True(situacao.Pura);
        Assert.False(situacao.CaixaAberto);
        Assert.False(situacao.ModoTeste);
        Assert.Null(a.Caixa.SessaoAberta(1));
        Assert.Null(a.Caixa.SessaoTesteAberta(1));
        // Como se as vendas não tivessem acontecido
        Assert.Equal(50, Estoque(pastel));
        Assert.Equal(10, Estoque(combo));
        Assert.Equal(100, Estoque(cerveja));
        // A numeração recomeça, também a do teste
        Assert.Equal(1, Vender(a.Caixa.Abrir(1, null, 0), pastel, 1).Numero);
    }

    [Fact]
    public void Restaurar_e_deixar_como_nova_funcionam_com_o_caixa_aberto()
    {
        ProgramarA();
        var arquivo = Path.Combine(_a.Pasta, "a.bcf");
        _a.Sistema.Programacao.Salvar(arquivo);
        var b = _b.Sistema;
        b.Caixa.Abrir(1, null, 0);
        b.Caixa.Abrir(1, null, 0, teste: true);

        b.Programacao.Carregar(arquivo, 5);
        Assert.True(b.Programacao.Situacao().Pura);
        Assert.Null(b.Caixa.SessaoAberta(5));

        b.Caixa.Abrir(5, null, 0);
        b.Programacao.DeixarComoNova();
        Assert.True(b.Programacao.Situacao().Pura);
        Assert.Empty(b.Catalogo.Produtos());
    }

    [Fact]
    public void Procura_os_backups_nas_pastas_do_mais_novo_para_o_mais_velho()
    {
        var a = _a.Sistema;
        var pasta = Path.Combine(_a.Pasta, "backup");
        var pendrive = Path.Combine(_a.Pasta, "pendrive");
        Directory.CreateDirectory(pasta);
        Directory.CreateDirectory(pendrive);
        void Salvar(string evento, string onde)
        {
            var config = a.Config.Atual.Clonar();
            config.NomeEvento = evento;
            a.Config.Salvar(config);
            a.Programacao.Salvar(Path.Combine(onde, a.Programacao.NomeArquivo()));
            Thread.Sleep(20);
        }
        Salvar("FESTA VELHA", pasta);
        Salvar("FESTA NOVA", pendrive);
        File.WriteAllText(Path.Combine(pasta, "defeito.bcf"), "não sou um backup");
        File.Copy(Path.Combine(pasta, "BCFichas - FESTA VELHA.bcf"), Path.Combine(pasta, "outro.bcfx"));

        var achados = a.Programacao.Procurar([
            (pasta, "Pasta do backup"), (pendrive, "Pendrive E:"), (pasta, "Pasta repetida"),
            (Path.Combine(_a.Pasta, "nao existe"), "Pendrive F:"),
        ]);

        Assert.Equal(["FESTA NOVA", "FESTA VELHA"], achados.Select(x => x.Resumo.Evento));
        Assert.Equal(["Pendrive E:", "Pasta do backup"], achados.Select(x => x.Lugar));
        Assert.EndsWith("BCFichas - FESTA VELHA.bcf", achados[1].Arquivo);
    }

    [Fact]
    public void Recusa_arquivo_que_nao_e_programacao()
    {
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
