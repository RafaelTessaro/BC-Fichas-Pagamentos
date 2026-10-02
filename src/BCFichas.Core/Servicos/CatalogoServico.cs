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

    public List<Aba> Abas() =>
        _banco.Consultar("SELECT id, nome, ordem FROM abas ORDER BY ordem, id", l => new Aba
        {
            Id = l.GetInt64(0),
            Nome = l.GetString(1),
            Ordem = l.GetInt32(2),
        });

    public Aba SalvarAba(Aba aba)
    {
        aba.Nome = (aba.Nome ?? "").Trim().ToUpperInvariant();
        if (aba.Nome.Length == 0) throw new ErroDeNegocio("Informe o nome da aba.");

        if (aba.Id == 0)
        {
            aba.Ordem = (int)(_banco.Escalar<long?>("SELECT MAX(ordem) FROM abas") ?? 0) + 1;
            aba.Id = _banco.Escalar<long>(
                "INSERT INTO abas (nome, ordem) VALUES ($n, $o) RETURNING id",
                ("$n", aba.Nome), ("$o", aba.Ordem));
        }
        else
        {
            _banco.Executar("UPDATE abas SET nome = $n, ordem = $o WHERE id = $id",
                ("$n", aba.Nome), ("$o", aba.Ordem), ("$id", aba.Id));
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
        sql += " ORDER BY aba_id, posicao, nome";
        return _banco.Consultar(sql, LerProduto, parametros.ToArray());
    }

    /// <summary>Produtos ativos de uma aba, para os botões da tela de venda.</summary>
    public List<Produto> ProdutosDaAba(long abaId) =>
        _banco.Consultar($"SELECT {ColunasProduto} FROM produtos WHERE aba_id = $a AND ativo = 1 ORDER BY posicao",
            LerProduto, ("$a", abaId));

    public Produto? Produto(long id) =>
        _banco.Consultar($"SELECT {ColunasProduto} FROM produtos WHERE id = $id", LerProduto, ("$id", id))
            .FirstOrDefault();

    public List<int> PosicoesLivres(long abaId, int totalPosicoes, long? ignorarProdutoId = null)
    {
        var ocupadas = _banco.Consultar(
            "SELECT posicao FROM produtos WHERE aba_id = $a AND id <> $p",
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
        if (produto.FichasPorUnidade is < 1 or > 20)
            throw new ErroDeNegocio("Fichas por unidade deve ser de 1 a 20.");
        if (produto.ControlaEstoque && produto.Estoque < 0)
            throw new ErroDeNegocio("O estoque não pode ser negativo.");
        if (produto.Posicao < 1 || produto.Posicao > totalPosicoes)
            throw new ErroDeNegocio($"Escolha uma posição de 1 a {totalPosicoes}.");
        if (!Abas().Any(a => a.Id == produto.AbaId))
            throw new ErroDeNegocio("Escolha a aba do produto.");

        var ocupante = _banco.Consultar(
            "SELECT nome FROM produtos WHERE aba_id = $a AND posicao = $p AND id <> $id",
            l => l.GetString(0), ("$a", produto.AbaId), ("$p", produto.Posicao), ("$id", produto.Id)).FirstOrDefault();
        if (ocupante is not null)
            throw new ErroDeNegocio($"A posição {produto.Posicao} desta aba já é do produto {ocupante}.");

        (string, object?)[] p =
        [
            ("$nome", produto.Nome), ("$detalhe", produto.Detalhe), ("$aba", produto.AbaId),
            ("$pos", produto.Posicao), ("$fichas", produto.FichasPorUnidade), ("$custo", produto.CustoCentavos),
            ("$preco", produto.PrecoCentavos), ("$ce", produto.ControlaEstoque ? 1 : 0), ("$est", produto.Estoque),
            ("$cor", produto.Cor), ("$img", produto.Imagem), ("$ativo", produto.Ativo ? 1 : 0), ("$id", produto.Id),
        ];

        if (produto.Id == 0)
        {
            produto.Id = _banco.Escalar<long>("""
                INSERT INTO produtos (nome, detalhe, aba_id, posicao, fichas_por_unidade, custo, preco,
                                      controla_estoque, estoque, cor, imagem, ativo)
                VALUES ($nome, $detalhe, $aba, $pos, $fichas, $custo, $preco, $ce, $est, $cor, $img, $ativo)
                RETURNING id
                """, p);
        }
        else
        {
            _banco.Executar("""
                UPDATE produtos SET nome = $nome, detalhe = $detalhe, aba_id = $aba, posicao = $pos,
                    fichas_por_unidade = $fichas, custo = $custo, preco = $preco, controla_estoque = $ce,
                    estoque = $est, cor = $cor, imagem = $img, ativo = $ativo
                WHERE id = $id
                """, p);
        }

        Alterado?.Invoke();
        return produto;
    }

    public void ExcluirProduto(long id)
    {
        _banco.Executar("DELETE FROM produtos WHERE id = $id", ("$id", id));
        Alterado?.Invoke();
    }

    /// <summary>Cria abas e produtos de exemplo num banco novo, para o sistema não abrir vazio.</summary>
    public void CriarExemplos()
    {
        var comidas = SalvarAba(new Aba { Nome = "Comidas" });
        var bebidas = SalvarAba(new Aba { Nome = "Bebidas" });
        var doces = SalvarAba(new Aba { Nome = "Doces" });

        (Aba aba, string nome, string detalhe, long preco, string cor)[] exemplos =
        [
            (comidas, "Pastel", "Carne ou queijo", 1000, "#E8590C"),
            (comidas, "Espetinho", "", 1200, "#C92A2A"),
            (comidas, "Cachorro-quente", "", 1000, "#D9480F"),
            (comidas, "Porção de batata", "", 2000, "#F08C00"),
            (comidas, "Pizza (fatia)", "", 800, "#E67700"),
            (comidas, "Caldo", "Feijão ou mandioca", 1200, "#A61E4D"),
            (bebidas, "Refrigerante", "Lata", 600, "#1971C2"),
            (bebidas, "Água", "Com ou sem gás", 400, "#1098AD"),
            (bebidas, "Suco", "", 700, "#2B8A3E"),
            (bebidas, "Cerveja", "Lata", 800, "#E8B200"),
            (bebidas, "Quentão", "", 700, "#862E9C"),
            (doces, "Bolo", "Fatia", 500, "#9C36B5"),
            (doces, "Pipoca", "", 500, "#F59F00"),
            (doces, "Algodão doce", "", 600, "#D6336C"),
            (doces, "Paçoca", "", 200, "#A0522D"),
        ];

        var posicoes = new Dictionary<long, int>();
        foreach (var (aba, nome, detalhe, preco, cor) in exemplos)
        {
            posicoes[aba.Id] = posicoes.GetValueOrDefault(aba.Id) + 1;
            SalvarProduto(new Produto
            {
                Nome = nome,
                Detalhe = detalhe,
                AbaId = aba.Id,
                Posicao = posicoes[aba.Id],
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
