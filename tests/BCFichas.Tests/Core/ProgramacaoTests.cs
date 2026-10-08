using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Backup da programação para outras máquinas, deixar a máquina pura e zerar a programação para um novo evento.</summary>
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
    public void Zerar_programacao_apaga_tudo_menos_impressora_numero_e_senha()
    {
        ProgramarA();
        var a = _a.Sistema;
        var config = a.Config.Atual.Clonar();
        config.NumeroCaixa = 4;
        config.AjusteHorizontal = 12;
        a.Config.Salvar(config);

        a.Programacao.ZerarProgramacao();

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
        Assert.Single(Directory.GetFiles(a.Programacao.PastaBackups, "*antes-de-zerar-programacao.db"));
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
        Assert.Equal(2, situacao.PedidosNoCaixaAberto); // o crédito esperando a maquininha ainda não é venda
        Assert.True(situacao.ModoTeste);
        Assert.Equal(2 + 2 + 8, situacao.EstoqueAVoltar); // 2 pastéis, 2 combos e 8 cervejas ainda fora do estoque

        // O backup feito agora já sai puro: com o estoque de antes das vendas
        var arquivo = Path.Combine(_a.Pasta, "pura.bcf");
        a.Programacao.Salvar(arquivo);
        _b.Sistema.Programacao.Carregar(arquivo, 2);
        Assert.Equal([50, 10, 100], new[] { pastel, combo, cerveja }.Select(p => _b.Sistema.Catalogo.Produto(p.Id)!.Estoque));

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
        Assert.Equal(1, Vender(a.Caixa.Abrir(1, null, 0, teste: true), pastel, 1).Numero);
        a.Caixa.ApagarTestes();
        Assert.Equal(1, Vender(a.Caixa.Abrir(1, null, 0), pastel, 1).Numero);
    }

    [Fact]
    public void So_volta_para_o_estoque_o_que_saiu_dele_e_da_para_deixar_como_esta()
    {
        var a = _a.Sistema;
        var pastel = a.Catalogo.Produtos().First(x => x.Nome == "PASTEL");
        var cerveja = a.Catalogo.Produtos().First(x => x.Nome == "CERVEJA");
        var refri = a.Catalogo.Produtos().First(x => x.Nome == "REFRIGERANTE");
        void Estoque(Produto p, bool controla, int estoque)
        {
            var atual = a.Catalogo.Produto(p.Id)!;
            atual.ControlaEstoque = controla;
            atual.Estoque = estoque;
            a.Catalogo.SalvarProduto(atual, 30);
        }
        int Atual(Produto p) => a.Catalogo.Produto(p.Id)!.Estoque;
        var sessao = a.Caixa.Abrir(1, null, 0);
        Pedido Vender(Produto p, int quantidade)
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(a.Catalogo.Produto(p.Id)!, quantidade);
            return a.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 100_000);
        }
        void Devolver(Pedido pedido, int quantidade) =>
            a.Devolucoes.Devolver(sessao, pedido.Id, new Dictionary<long, int> { [pedido.Itens[0].Id] = quantidade }, null);

        // Vendido sem controle de estoque e só depois o estoque foi ligado: não tirou nada, não devolve nada
        var pasteis = Vender(pastel, 3);
        Estoque(pastel, true, 40);
        Devolver(pasteis, 1);
        Assert.Equal(40, Atual(pastel));
        // Vendido e depois o estoque foi contado e digitado de novo: vale a contagem
        Estoque(cerveja, true, 100);
        var cervejas = Vender(cerveja, 5);
        Estoque(cerveja, true, 200);
        Devolver(cervejas, 2);
        Assert.Equal(200, Atual(cerveja));
        // Vendido e ninguém mexeu no estoque: volta o que saiu (a ficha devolvida já voltou)
        Estoque(refri, true, 30);
        var refris = Vender(refri, 4);
        Devolver(refris, 1);
        Assert.Equal(27, Atual(refri));
        Assert.Equal(3, a.Programacao.Situacao().EstoqueAVoltar);

        // Outra festa com o que sobrou: o estoque fica como está
        a.Programacao.ApagarVendas(devolverEstoque: false);
        Assert.Equal([40, 200, 27], new[] { pastel, cerveja, refri }.Select(Atual));

        sessao = a.Caixa.Abrir(1, null, 0);
        Vender(refri, 6);
        a.Programacao.ApagarVendas();
        Assert.Equal([40, 200, 27], new[] { pastel, cerveja, refri }.Select(Atual));
    }

    [Fact]
    public void Vendas_de_antes_da_versao_37_tambem_voltam_para_o_estoque()
    {
        var a = _a.Sistema;
        var cerveja = a.Catalogo.Produtos().First(x => x.Nome == "CERVEJA");
        cerveja.ControlaEstoque = true;
        cerveja.Estoque = 100;
        a.Catalogo.SalvarProduto(cerveja, 30);
        a.Catalogo.SalvarProduto(new Produto
        {
            Nome = "Combo cerveja", AbaId = cerveja.AbaId, Posicao = a.Catalogo.PosicoesLivres(cerveja.AbaId, 30).First(),
            PrecoCentavos = 3500,
            Componentes = [new ComponenteCombo { ProdutoId = cerveja.Id, Nome = "CERVEJA", Quantidade = 5, ValorCentavos = 800 }],
        }, 30);
        var combo = a.Catalogo.Produtos().First(x => x.Nome == "COMBO CERVEJA");
        var sessao = a.Caixa.Abrir(1, null, 0);
        foreach (var (produto, quantidade) in new[] { (cerveja, 3), (combo, 1) })
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(a.Catalogo.Produto(produto.Id)!, quantidade);
            a.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 100_000);
        }
        Assert.Equal(92, a.Catalogo.Produto(cerveja.Id)!.Estoque);
        // Volta o banco para a versão 4 (a 3.6 não guardava quanto cada venda tirou do estoque)
        a.Banco.Executar("""
            ALTER TABLE itens_pedido DROP COLUMN baixado; ALTER TABLE componentes_item DROP COLUMN baixado;
            DELETE FROM versao WHERE v >= 5;
            """);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var atualizado = Sistema.Iniciar(_a.Pasta, criarExemplos: false);
        Assert.Equal(8, atualizado.Programacao.Situacao().EstoqueAVoltar);
        atualizado.Programacao.ApagarVendas();
        Assert.Equal(100, atualizado.Catalogo.Produto(cerveja.Id)!.Estoque);
    }

    [Fact]
    public void Restaura_combo_que_vem_antes_do_produto_das_fichas()
    {
        var a = _a.Sistema;
        var aba = a.Catalogo.Abas().First();
        // O produto das fichas fica escondido (só sai pelo combo): vem depois do combo na lista
        var heineken = a.Catalogo.SalvarProduto(new Produto
        {
            Nome = "Heineken", AbaId = aba.Id, PrecoCentavos = 650, Ativo = false,
        }, 30);
        var livre = a.Catalogo.PosicoesLivres(aba.Id, 30).First();
        a.Catalogo.SalvarProduto(new Produto
        {
            Nome = "Combo Heineken", AbaId = aba.Id, Posicao = livre, PrecoCentavos = 3000,
            Componentes = [new ComponenteCombo { ProdutoId = heineken.Id, Nome = "HEINEKEN", Quantidade = 5, ValorCentavos = 650 }],
        }, 30);
        var produtos = a.Catalogo.Produtos();
        Assert.True(produtos.FindIndex(p => p.Nome == "COMBO HEINEKEN") < produtos.FindIndex(p => p.Nome == "HEINEKEN"));
        var arquivo = Path.Combine(_a.Pasta, "combo.bcf");
        a.Programacao.Salvar(arquivo);

        _b.Sistema.Programacao.Carregar(arquivo, 2);

        var combo = _b.Sistema.Catalogo.Produtos().Single(p => p.Nome == "COMBO HEINEKEN");
        Assert.Equal(heineken.Id, Assert.Single(combo.Componentes).ProdutoId);
    }

    [Fact]
    public void Grade_de_cada_aba_vai_no_backup_e_backup_antigo_usa_a_das_configuracoes()
    {
        var a = _a.Sistema;
        var abas = a.Catalogo.Abas();
        abas[0].Colunas = 5;
        abas[0].Linhas = 2;
        a.Catalogo.SalvarAba(abas[0]);
        var config = a.Config.Atual.Clonar();
        config.Colunas = 3;
        config.Linhas = 4;
        a.Config.Salvar(config);
        var arquivo = Path.Combine(_a.Pasta, "grade.bcf");
        a.Programacao.Salvar(arquivo);

        _b.Sistema.Programacao.Carregar(arquivo, 2);
        var restauradas = _b.Sistema.Catalogo.Abas();
        Assert.Equal((5, 2), (restauradas[0].Colunas, restauradas[0].Linhas));
        Assert.True(restauradas[1].Automatica);

        // Backup feito antes da 3.10 (sem a grade nas abas): as abas ficam com a grade das configurações dele
        using (var zip = System.IO.Compression.ZipFile.Open(arquivo, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entrada = zip.GetEntry("programacao.json")!;
            string json;
            using (var leitura = new StreamReader(entrada.Open())) json = leitura.ReadToEnd();
            var raiz = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            foreach (var aba in raiz["Abas"]!.AsArray())
            {
                aba!.AsObject().Remove("Colunas");
                aba.AsObject().Remove("Linhas");
            }
            entrada.Delete();
            using var escrita = new StreamWriter(zip.CreateEntry("programacao.json").Open());
            escrita.Write(raiz.ToJsonString());
        }
        _b.Sistema.Programacao.Carregar(arquivo, 2);
        Assert.All(_b.Sistema.Catalogo.Abas(), x => Assert.Equal((3, 4), (x.Colunas, x.Linhas)));
    }

    [Fact]
    public void Banco_antigo_passa_a_grade_das_configuracoes_para_cada_aba()
    {
        var a = _a.Sistema;
        var config = a.Config.Atual.Clonar();
        config.Colunas = 5;
        config.Linhas = 2;
        a.Config.Salvar(config);
        // Volta o banco para a versão 5 (antes da 3.10 a grade era uma só, nas configurações)
        a.Banco.Executar("ALTER TABLE abas DROP COLUMN colunas; ALTER TABLE abas DROP COLUMN linhas; DELETE FROM versao WHERE v >= 6;");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var atualizado = Sistema.Iniciar(_a.Pasta, criarExemplos: false);
        Assert.NotEmpty(atualizado.Catalogo.Abas());
        Assert.All(atualizado.Catalogo.Abas(), x => Assert.Equal((5, 2), (x.Colunas, x.Linhas)));
        // Aba nova já nasce automática
        Assert.True(atualizado.Catalogo.SalvarAba(new Aba { Nome = "Nova" }).Automatica);
    }

    [Fact]
    public void Backup_que_deu_errado_antes_nao_atrapalha_o_proximo()
    {
        var arquivo = Path.Combine(_a.Pasta, "festa.bcf");
        File.WriteAllText(arquivo + ".tmp", "sobrou de uma vez que a bateria acabou");
        _a.Sistema.Programacao.Salvar(arquivo);
        Assert.False(File.Exists(arquivo + ".tmp"));
        Assert.Equal(_a.Sistema.Config.Atual.NomeEvento, _a.Sistema.Programacao.Resumo(arquivo).Evento);
    }

    [Fact]
    public void Restaurar_e_zerar_programacao_funcionam_com_o_caixa_aberto()
    {
        ProgramarA();
        var arquivo = Path.Combine(_a.Pasta, "a.bcf");
        _a.Sistema.Programacao.Salvar(arquivo);
        var b = _b.Sistema;
        b.Caixa.Abrir(1, null, 0);
        b.Caixa.Abrir(1, null, 0, teste: true);

        b.Programacao.Carregar(arquivo, 5);
        Assert.True(b.Programacao.Situacao().Pura);
        Assert.Null(b.Caixa.SessaoAberta(1));
        Assert.Null(b.Caixa.SessaoTesteAberta(1));

        b.Caixa.Abrir(5, null, 0);
        b.Programacao.ZerarProgramacao();
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
        // Backup de uma versão mais nova do programa
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(pendrive, "nova.bcf"),
                   System.IO.Compression.ZipArchiveMode.Create))
        using (var escrita = new StreamWriter(zip.CreateEntry("programacao.json").Open()))
            escrita.Write("""{"Formato":99,"Evento":"DO FUTURO"}""");

        var procura = a.Programacao.Procurar([
            (pasta, "Pasta do backup"), (pendrive, "Pendrive E:"), (pasta, "Pasta repetida"),
            (Path.Combine(_a.Pasta, "nao existe"), "Pendrive F:"),
        ]);

        var achados = procura.Achados;
        Assert.Equal(["FESTA NOVA", "FESTA VELHA"], achados.Select(x => x.Resumo.Evento));
        Assert.Equal(["Pendrive E:", "Pasta do backup"], achados.Select(x => x.Lugar));
        Assert.EndsWith("BCFichas - FESTA VELHA.bcf", achados[1].Arquivo);
        // Os que não dá para usar aparecem com o motivo (não somem)
        Assert.Equal(["defeito.bcf", "nova.bcf"], procura.Recusados.Select(r => Path.GetFileName(r.Arquivo)).Order());
        Assert.Contains("ainda está sendo copiado", procura.Recusados.Single(r => r.Arquivo.EndsWith("defeito.bcf")).Motivo);
        Assert.Contains("versão mais nova", procura.Recusados.Single(r => r.Arquivo.EndsWith("nova.bcf")).Motivo);
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
