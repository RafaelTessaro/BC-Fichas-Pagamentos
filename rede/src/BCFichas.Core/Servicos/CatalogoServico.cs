using BCFichas.Core.Dados;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>Abas e produtos (os botões da tela de venda).</summary>
public sealed class CatalogoServico
{
    private const string ColunasProduto =
        "id, nome, detalhe, aba_id, posicao, fichas_por_unidade, custo, preco, controla_estoque, estoque, cor, imagem, ativo";

    private readonly Banco _banco;

    public CatalogoServico(Banco banco) => _banco = banco;

    public event Action? Alterado;

    internal void AvisarAlteracao() => Alterado?.Invoke();

    public List<Aba> Abas() =>
        _banco.Consultar("SELECT id, nome, ordem, colunas, linhas FROM abas ORDER BY ordem, id", l => new Aba
        {
            Id = l.GetInt64(0),
            Nome = l.GetString(1),
            Ordem = l.GetInt32(2),
            Colunas = l.GetInt32(3),
            Linhas = l.GetInt32(4),
        });

    public const int MaximoColunas = 6;
    public const int MaximoLinhas = 8;

    public Aba SalvarAba(Aba aba)
    {
        aba.Nome = (aba.Nome ?? "").Trim().ToUpperInvariant();
        if (aba.Nome.Length == 0) throw new ErroDeNegocio("Informe o nome da aba.");
        // Automática (0) ou de 1 a 6 colunas por 1 a 8 linhas
        if (aba.Colunas <= 0)
        {
            aba.Colunas = 0;
            aba.Linhas = 0;
        }
        else
        {
            aba.Colunas = Math.Min(aba.Colunas, MaximoColunas);
            aba.Linhas = Math.Clamp(aba.Linhas, 1, MaximoLinhas);
        }

        if (aba.Id == 0)
        {
            aba.Ordem = (int)(_banco.Escalar<long?>("SELECT MAX(ordem) FROM abas") ?? 0) + 1;
            aba.Id = _banco.Escalar<long>(
                "INSERT INTO abas (nome, ordem, colunas, linhas) VALUES ($n, $o, $c, $l) RETURNING id",
                ("$n", aba.Nome), ("$o", aba.Ordem), ("$c", aba.Colunas), ("$l", aba.Linhas));
        }
        else
        {
            _banco.Executar("UPDATE abas SET nome = $n, ordem = $o, colunas = $c, linhas = $l WHERE id = $id",
                ("$n", aba.Nome), ("$o", aba.Ordem), ("$c", aba.Colunas), ("$l", aba.Linhas), ("$id", aba.Id));
        }

        Alterado?.Invoke();
        return aba;
    }

    public void ExcluirAba(long abaId)
    {
        var produtos = _banco.Escalar<long>("SELECT COUNT(*) FROM produtos WHERE aba_id = $id", ("$id", abaId));
        if (produtos > 0)
            throw new ErroDeNegocio("Esta aba ainda tem produtos. Mova ou exclua os produtos antes.");
        if (_banco.Escalar<long>("SELECT COUNT(*) FROM abas") <= 1)
            throw new ErroDeNegocio("É preciso ter pelo menos uma aba.");

        _banco.Executar("DELETE FROM abas WHERE id = $id", ("$id", abaId));
        Alterado?.Invoke();
    }

    public void ReordenarAbas(IReadOnlyList<long> idsNaOrdem)
    {
        _banco.Transacao((c, t) =>
        {
            for (var i = 0; i < idsNaOrdem.Count; i++)
                Banco.Executar(c, t, "UPDATE abas SET ordem = $o WHERE id = $id", ("$o", i + 1), ("$id", idsNaOrdem[i]));
        });
        Alterado?.Invoke();
    }

    public List<Produto> Produtos(string? busca = null)
    {
        var sql = $"SELECT {ColunasProduto} FROM produtos";
        var parametros = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(busca))
        {
            sql += " WHERE nome LIKE $b OR detalhe LIKE $b";
            parametros.Add(("$b", "%" + busca.Trim() + "%"));
        }
        sql += " ORDER BY aba_id, ativo DESC, posicao, nome";
        return ComComponentes(_banco.Consultar(sql, LerProduto, parametros.ToArray()));
    }

    /// <summary>Produtos ativos de uma aba, para os botões da tela de venda.</summary>
    public List<Produto> ProdutosDaAba(long abaId) =>
        ComComponentes(_banco.Consultar(
            $"SELECT {ColunasProduto} FROM produtos WHERE aba_id = $a AND ativo = 1 ORDER BY posicao",
            LerProduto, ("$a", abaId)));

    public Produto? Produto(long id) =>
        ComComponentes(_banco.Consultar($"SELECT {ColunasProduto} FROM produtos WHERE id = $id", LerProduto, ("$id", id)))
            .FirstOrDefault();

    /// <summary>Combos que têm este produto nas fichas.</summary>
    public List<string> CombosQueUsam(long produtoId) =>
        _banco.Consultar("""
            SELECT DISTINCT p.nome FROM componentes_combo c JOIN produtos p ON p.id = c.combo_id
            WHERE c.produto_id = $p ORDER BY p.nome
            """, l => l.GetString(0), ("$p", produtoId));

    private const string SqlComponentes = """
        SELECT c.combo_id, c.id, c.produto_id, c.nome, c.detalhe, c.quantidade, c.valor,
               COALESCE(p.controla_estoque, 0), COALESCE(p.estoque, 0)
        FROM componentes_combo c LEFT JOIN produtos p ON p.id = c.produto_id
        """;

    internal static ComponenteCombo LerComponente(SqliteDataReader l) => new()
    {
        Id = l.GetInt64(1),
        ProdutoId = l.IsDBNull(2) ? null : l.GetInt64(2),
        Nome = l.GetString(3),
        Detalhe = l.GetString(4),
        Quantidade = l.GetInt32(5),
        ValorCentavos = l.GetInt64(6),
        ControlaEstoque = l.GetInt64(7) != 0,
        Estoque = l.GetInt32(8),
    };

    /// <summary>Fichas de um combo, na ordem cadastrada (lidas na mesma transação da venda).</summary>
    internal static List<ComponenteCombo> Componentes(SqliteConnection c, SqliteTransaction? t, long comboId) =>
        Banco.Consultar(c, t, SqlComponentes + " WHERE c.combo_id = $c ORDER BY c.ordem, c.id", LerComponente,
            ("$c", comboId));

    private List<Produto> ComComponentes(List<Produto> produtos)
    {
        if (produtos.Count == 0) return produtos;
        var porCombo = _banco.Consultar(SqlComponentes + " ORDER BY c.combo_id, c.ordem, c.id",
                l => (Combo: l.GetInt64(0), Componente: LerComponente(l)))
            .ToLookup(x => x.Combo, x => x.Componente);
        foreach (var p in produtos) p.Componentes = porCombo[p.Id].ToList();
        return produtos;
    }

    public List<int> PosicoesLivres(long abaId, int totalPosicoes, long? ignorarProdutoId = null)
    {
        var ocupadas = _banco.Consultar(
            "SELECT posicao FROM produtos WHERE aba_id = $a AND id <> $p AND ativo = 1",
            l => l.GetInt32(0), ("$a", abaId), ("$p", ignorarProdutoId ?? 0)).ToHashSet();
        return Enumerable.Range(1, totalPosicoes).Where(p => !ocupadas.Contains(p)).ToList();
    }

    public Produto SalvarProduto(Produto produto, int totalPosicoes)
    {
        produto.Nome = (produto.Nome ?? "").Trim().ToUpperInvariant();
        produto.Detalhe = (produto.Detalhe ?? "").Trim().ToUpperInvariant();

        if (produto.Nome.Length == 0) throw new ErroDeNegocio("Informe o nome do produto.");
        if (produto.Nome.Length > 40) throw new ErroDeNegocio("O nome do produto pode ter no máximo 40 letras.");
        if (produto.PrecoCentavos < 0) throw new ErroDeNegocio("O preço não pode ser negativo.");
        if (produto.CustoCentavos < 0) throw new ErroDeNegocio("O custo não pode ser negativo.");
        if (produto.EhCombo) produto.FichasPorUnidade = 1;
        if (produto.FichasPorUnidade is < 1 or > 20)
            throw new ErroDeNegocio("Fichas por unidade deve ser de 1 a 20.");
        if (produto.ControlaEstoque && produto.Estoque < 0)
            throw new ErroDeNegocio("O estoque não pode ser negativo.");
        if (!Abas().Any(a => a.Id == produto.AbaId))
            throw new ErroDeNegocio("Escolha a aba do produto.");
        ConferirCombo(produto);

        if (produto.Ativo)
        {
            if (produto.Posicao < 1 || produto.Posicao > totalPosicoes)
                throw new ErroDeNegocio($"Escolha uma posição de 1 a {totalPosicoes}.");
            var ocupante = _banco.Consultar(
                "SELECT nome FROM produtos WHERE aba_id = $a AND posicao = $p AND id <> $id AND ativo = 1",
                l => l.GetString(0), ("$a", produto.AbaId), ("$p", produto.Posicao), ("$id", produto.Id)).FirstOrDefault();
            if (ocupante is not null)
                throw new ErroDeNegocio($"A posição {produto.Posicao} desta aba já é do produto {ocupante}.");
        }
        else
        {
            // Escondido da tela de venda: não ocupa lugar na grade de botões.
            produto.Posicao = 0;
        }

        (string, object?)[] p =
        [
            ("$nome", produto.Nome), ("$detalhe", produto.Detalhe), ("$aba", produto.AbaId),
            ("$pos", produto.Posicao), ("$fichas", produto.FichasPorUnidade), ("$custo", produto.CustoCentavos),
            ("$preco", produto.PrecoCentavos), ("$ce", produto.ControlaEstoque ? 1 : 0), ("$est", produto.Estoque),
            ("$cor", produto.Cor), ("$img", produto.Imagem), ("$ativo", produto.Ativo ? 1 : 0), ("$id", produto.Id),
        ];

        _banco.Transacao((c, t) =>
        {
            if (produto.Id == 0)
            {
                produto.Id = Banco.Escalar<long>(c, t, """
                    INSERT INTO produtos (nome, detalhe, aba_id, posicao, fichas_por_unidade, custo, preco,
                                          controla_estoque, estoque, cor, imagem, ativo)
                    VALUES ($nome, $detalhe, $aba, $pos, $fichas, $custo, $preco, $ce, $est, $cor, $img, $ativo)
                    RETURNING id
                    """, p);
            }
            else
            {
                // Estoque digitado de novo (ou controle ligado/desligado): é a contagem que vale agora, então as
                // vendas de antes não voltam para ele ao "Apagar as vendas".
                var antes = Banco.Consultar(c, t, "SELECT controla_estoque, estoque FROM produtos WHERE id = $id",
                    l => (Controla: l.GetInt64(0) == 1, Estoque: l.GetInt64(1)), ("$id", produto.Id)).FirstOrDefault();
                if (antes.Controla != produto.ControlaEstoque || antes.Estoque != produto.Estoque)
                    Banco.Executar(c, t, """
                        UPDATE itens_pedido SET baixado = 0 WHERE produto_id = $id AND baixado <> 0;
                        UPDATE componentes_item SET baixado = 0 WHERE produto_id = $id AND baixado <> 0;
                        """, ("$id", produto.Id));
                Banco.Executar(c, t, """
                    UPDATE produtos SET nome = $nome, detalhe = $detalhe, aba_id = $aba, posicao = $pos,
                        fichas_por_unidade = $fichas, custo = $custo, preco = $preco, controla_estoque = $ce,
                        estoque = $est, cor = $cor, imagem = $img, ativo = $ativo
                    WHERE id = $id
                    """, p);
            }

            Banco.Executar(c, t, "DELETE FROM componentes_combo WHERE combo_id = $c", ("$c", produto.Id));
            for (var i = 0; i < produto.Componentes.Count; i++)
            {
                var componente = produto.Componentes[i];
                componente.Id = Banco.Escalar<long>(c, t, """
                    INSERT INTO componentes_combo (combo_id, produto_id, nome, detalhe, quantidade, valor, ordem)
                    VALUES ($c, $p, $n, $d, $q, $v, $o) RETURNING id
                    """, ("$c", produto.Id), ("$p", componente.ProdutoId), ("$n", componente.Nome),
                    ("$d", componente.Detalhe), ("$q", componente.Quantidade), ("$v", componente.ValorCentavos),
                    ("$o", i + 1));
            }
        });

        Alterado?.Invoke();
        return produto;
    }

    /// <summary>
    /// Regras do combo: fichas de produtos que existem, nada de combo dentro de combo, quantidades e valores
    /// que fazem sentido.
    /// </summary>
    private void ConferirCombo(Produto produto)
    {
        if (!produto.EhCombo) return;
        if (produto.Componentes.Count > 40) throw new ErroDeNegocio("O combo pode ter no máximo 40 linhas.");
        if (produto.Id != 0)
        {
            var usado = CombosQueUsam(produto.Id);
            if (usado.Count > 0)
                throw new ErroDeNegocio($"{produto.Nome} sai nas fichas do combo {usado[0]}; um combo não pode ter outro combo dentro.");
        }
        foreach (var componente in produto.Componentes)
        {
            componente.Nome = (componente.Nome ?? "").Trim().ToUpperInvariant();
            componente.Detalhe = (componente.Detalhe ?? "").Trim().ToUpperInvariant();
            if (componente.Nome.Length == 0) throw new ErroDeNegocio("Escreva o nome que sai na ficha do combo.");
            if (componente.Nome.Length > 40 || componente.Detalhe.Length > 40)
                throw new ErroDeNegocio($"{componente.Nome}: nome e detalhe da ficha podem ter no máximo 40 letras.");
            if (componente.ProdutoId is { } produtoId)
            {
                if (produtoId == produto.Id && produto.Id != 0)
                    throw new ErroDeNegocio("O combo não pode ter ele mesmo nas fichas.");
                var item = Produto(produtoId)
                           ?? throw new ErroDeNegocio($"{componente.Nome}: o produto ligado a esta ficha foi excluído.");
                if (item.EhCombo)
                    throw new ErroDeNegocio($"{item.Nome} já é um combo; um combo não pode ter outro combo dentro.");
            }
            if (componente.Quantidade is < 1 or > 200)
                throw new ErroDeNegocio($"{componente.Nome}: a quantidade de fichas vai de 1 a 200.");
            if (componente.ValorCentavos < 0)
                throw new ErroDeNegocio($"{componente.Nome}: o valor da ficha não pode ser negativo.");
        }
        if (produto.Componentes.Sum(c => c.Quantidade) > 300)
            throw new ErroDeNegocio("O combo pode imprimir no máximo 300 fichas.");
    }

    /// <summary>
    /// Exclui o produto. Se ele sai nas fichas de algum combo, o combo continua igual (com o nome e o valor da
    /// ficha), só deixa de baixar o estoque dele.
    /// </summary>
    public void ExcluirProduto(long id)
    {
        _banco.Transacao((c, t) =>
        {
            Banco.Executar(c, t, "UPDATE componentes_combo SET produto_id = NULL WHERE produto_id = $id", ("$id", id));
            Banco.Executar(c, t, "DELETE FROM componentes_combo WHERE combo_id = $id", ("$id", id));
            Banco.Executar(c, t, "DELETE FROM produtos WHERE id = $id", ("$id", id));
        });
        Alterado?.Invoke();
    }

    /// <summary>
    /// Banco novo: cria a aba ITENS com os produtos de exemplo (os mesmos do sistema antigo),
    /// para o programa não abrir vazio.
    /// </summary>
    public void CriarExemplos()
    {
        var abas = Abas();
        var itens = abas.Count > 0 ? abas[0] : SalvarAba(new Aba { Nome = "ITENS" });

        (string nome, long preco, string cor)[] exemplos =
        [
            ("Pastel", 1000, "#E8590C"),
            ("Massinha", 1000, "#C92A2A"),
            ("Batata porção", 2000, "#F08C00"),
            ("Enroladinho porção", 2000, "#D9480F"),
            ("Pão de mel", 800, "#A0522D"),
            ("Heineken", 1000, "#2B8A3E"),
            ("Original", 1000, "#E8B200"),
            ("Brahma", 800, "#A61E4D"),
            ("Império", 800, "#475569"),
            ("Refrigerante", 600, "#1971C2"),
            ("Água", 400, "#1098AD"),
            ("Suco", 600, "#7C3AED"),
        ];

        for (var i = 0; i < exemplos.Length; i++)
        {
            var (nome, preco, cor) = exemplos[i];
            SalvarProduto(new Produto
            {
                Nome = nome,
                AbaId = itens.Id,
                Posicao = i + 1,
                PrecoCentavos = preco,
                Cor = cor,
            }, 36);
        }
    }

    internal static Produto LerProduto(SqliteDataReader l) => new()
    {
        Id = l.GetInt64(0),
        Nome = l.GetString(1),
        Detalhe = l.GetString(2),
        AbaId = l.GetInt64(3),
        Posicao = l.GetInt32(4),
        FichasPorUnidade = l.GetInt32(5),
        CustoCentavos = l.GetInt64(6),
        PrecoCentavos = l.GetInt64(7),
        ControlaEstoque = l.GetInt64(8) != 0,
        Estoque = l.GetInt32(9),
        Cor = l.GetString(10),
        Imagem = l.IsDBNull(11) ? null : l.GetString(11),
        Ativo = l.GetInt64(12) != 0,
    };
}
