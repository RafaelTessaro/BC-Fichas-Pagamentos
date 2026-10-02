using BCFichas.Core.Dados;
using BCFichas.Core.Vendas;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>
/// Devolução de fichas que o cliente não usou. A venda não é cancelada: só a parte devolvida sai do total.
/// Venda em dinheiro: o dinheiro sai da gaveta deste caixa. Cartão ou PIX: o estorno é feito na maquininha
/// (por causa das taxas) e aqui fica só o registro.
/// </summary>
public sealed class DevolucaoServico
{
    private readonly Banco _banco;
    private readonly CaixaServico _caixa;
    private readonly VendaServico _vendas;
    private readonly Func<DateTime> _agora;

    public DevolucaoServico(Banco banco, CaixaServico caixa, VendaServico vendas, Func<DateTime>? agora = null)
    {
        _banco = banco;
        _caixa = caixa;
        _vendas = vendas;
        _agora = agora ?? (() => DateTime.Now);
    }

    /// <summary>Devolve unidades de produtos vendidos sozinhos (id do item → quantidade).</summary>
    public Devolucao Devolver(SessaoCaixa sessao, long pedidoId, IReadOnlyDictionary<long, int> quantidades,
        string? motivo) =>
        Devolver(sessao, pedidoId, quantidades.Select(q => (q.Key, (long?)null, q.Value)).ToList(), motivo);

    /// <param name="sessao">Caixa aberto agora (de onde sai o dinheiro).</param>
    /// <param name="devolvidas">
    /// O que voltou: item do pedido, ficha do combo (nulo para o produto vendido sozinho) e quantidade.
    /// </param>
    public Devolucao Devolver(SessaoCaixa sessao, long pedidoId,
        IReadOnlyList<(long ItemId, long? ComponenteId, int Quantidade)> devolvidas, string? motivo)
    {
        if (!sessao.Aberta) throw new ErroDeNegocio("O caixa está fechado.");
        var pedido = _vendas.Pedido(pedidoId) ?? throw new ErroDeNegocio("Pedido não encontrado.");
        if (pedido.Status != StatusPedido.Pago) throw new ErroDeNegocio("Só dá para devolver fichas de pedido pago.");
        if (pedido.Teste != sessao.Teste)
            throw new ErroDeNegocio(pedido.Teste
                ? "Este pedido é do modo teste."
                : "No modo teste só dá para devolver fichas de teste.");

        var linhas = FichasDoPedido.Linhas(pedido);
        var itens = new List<ItemDevolvido>();
        var escolhidas = new List<(LinhaDeFichas Linha, ItemDevolvido Item)>();
        foreach (var (itemId, componenteId, quantidade) in devolvidas)
        {
            if (quantidade <= 0) continue;
            var linha = linhas.FirstOrDefault(l => l.Item.Id == itemId && l.Componente?.Id == componenteId)
                        ?? throw new ErroDeNegocio("Item não encontrado neste pedido.");
            var nome = linha.DeCombo ? $"{linha.Nome} ({linha.Item.Nome})" : linha.Nome;
            if (quantidade > linha.PodeDevolver)
                throw new ErroDeNegocio(linha.PodeDevolver == 0
                    ? $"{nome}: todas as fichas já foram devolvidas."
                    : $"{nome}: só dá para devolver {linha.PodeDevolver}.");
            var item = new ItemDevolvido
            {
                ItemId = itemId,
                ComponenteId = componenteId,
                Nome = nome,
                PrecoCentavos = linha.ValorUnitario,
                Quantidade = quantidade,
                TotalCentavos = linha.Valor(quantidade),
            };
            itens.Add(item);
            escolhidas.Add((linha, item));
        }
        if (itens.Count == 0) throw new ErroDeNegocio("Escolha as fichas que o cliente está devolvendo.");

        var devolucao = new Devolucao
        {
            SessaoId = sessao.Id,
            PedidoId = pedido.Id,
            NumeroPedido = pedido.Numero,
            Caixa = sessao.Caixa,
            Forma = pedido.Forma,
            ValorCentavos = itens.Sum(i => i.TotalCentavos),
            Motivo = (motivo ?? "").Trim().ToUpperInvariant(),
            CriadoEm = _agora(),
            Itens = itens,
        };

        if (devolucao.EmDinheiro)
        {
            var noCaixa = _caixa.Resumo(sessao.Id).DinheiroEsperado;
            if (devolucao.ValorCentavos > noCaixa)
                throw new ErroDeNegocio(
                    $"No caixa há só {Dinheiro.Formatar(noCaixa)} em dinheiro para devolver {Dinheiro.Formatar(devolucao.ValorCentavos)}.");
        }

        _banco.Transacao((c, t) =>
        {
            devolucao.Id = Banco.Escalar<long>(c, t, """
                INSERT INTO devolucoes (sessao_id, pedido_id, caixa, forma, valor, motivo, criado_em)
                VALUES ($s, $p, $c, $f, $v, $m, $d) RETURNING id
                """,
                ("$s", devolucao.SessaoId), ("$p", devolucao.PedidoId), ("$c", devolucao.Caixa),
                ("$f", (int)devolucao.Forma), ("$v", devolucao.ValorCentavos), ("$m", devolucao.Motivo),
                ("$d", Banco.Data(devolucao.CriadoEm)));

            foreach (var (linha, item) in escolhidas)
            {
                // Confere de novo dentro da transação (dois toques seguidos no botão não devolvem duas vezes).
                var jaDevolvidas = (int)(item.ComponenteId is { } componenteId
                    ? Banco.Escalar<long>(c, t,
                        "SELECT COALESCE(SUM(quantidade), 0) FROM itens_devolucao WHERE componente_id = $c", ("$c", componenteId))
                    : Banco.Escalar<long>(c, t,
                        "SELECT COALESCE(SUM(quantidade), 0) FROM itens_devolucao WHERE item_id = $i AND componente_id IS NULL",
                        ("$i", item.ItemId)));
                if (jaDevolvidas + item.Quantidade > linha.Total)
                    throw new ErroDeNegocio($"{item.Nome}: essas fichas já foram devolvidas.");
                item.TotalCentavos = linha.Valor(item.Quantidade, jaDevolvidas);

                Banco.Executar(c, t, """
                    INSERT INTO itens_devolucao (devolucao_id, item_id, componente_id, nome, preco, quantidade, valor)
                    VALUES ($d, $i, $c, $n, $p, $q, $v)
                    """,
                    ("$d", devolucao.Id), ("$i", item.ItemId), ("$c", item.ComponenteId), ("$n", item.Nome),
                    ("$p", item.PrecoCentavos), ("$q", item.Quantidade), ("$v", item.TotalCentavos));

                // A ficha voltou sem ser usada: o produto volta para o estoque (no teste o estoque não mexe).
                var produtoId = linha.Componente is { } componente ? componente.ProdutoId : linha.Item.ProdutoId;
                if (!pedido.Teste && produtoId is not null)
                    Banco.Executar(c, t,
                        "UPDATE produtos SET estoque = estoque + $q WHERE controla_estoque = 1 AND id = $p",
                        ("$q", item.Quantidade), ("$p", produtoId));
            }
            // O valor pode ter mudado um centavo se outra devolução entrou no meio.
            devolucao.ValorCentavos = itens.Sum(i => i.TotalCentavos);
            Banco.Executar(c, t, "UPDATE devolucoes SET valor = $v WHERE id = $id",
                ("$v", devolucao.ValorCentavos), ("$id", devolucao.Id));
        });

        _vendas.AvisarEstoque();
        return devolucao;
    }

    /// <summary>Devoluções feitas num caixa, da mais recente para a mais antiga.</summary>
    public List<Devolucao> Devolucoes(long sessaoId)
    {
        using var c = _banco.Abrir();
        var lista = Banco.Consultar(c, null, """
            SELECT d.id, d.sessao_id, d.pedido_id, p.numero, d.caixa, d.forma, d.valor, d.motivo, d.criado_em
            FROM devolucoes d JOIN pedidos p ON p.id = d.pedido_id
            WHERE d.sessao_id = $s ORDER BY d.id DESC
            """, Ler, ("$s", sessaoId));
        foreach (var d in lista) d.Itens = Itens(c, d.Id);
        return lista;
    }

    private static List<ItemDevolvido> Itens(SqliteConnection c, long devolucaoId) =>
        Banco.Consultar(c, null,
            "SELECT item_id, componente_id, nome, preco, quantidade, valor FROM itens_devolucao WHERE devolucao_id = $d ORDER BY id",
            l => new ItemDevolvido
            {
                ItemId = l.GetInt64(0),
                ComponenteId = l.IsDBNull(1) ? null : l.GetInt64(1),
                Nome = l.GetString(2),
                PrecoCentavos = l.GetInt64(3),
                Quantidade = l.GetInt32(4),
                TotalCentavos = l.GetInt64(5),
            }, ("$d", devolucaoId));

    private static Devolucao Ler(SqliteDataReader l) => new()
    {
        Id = l.GetInt64(0),
        SessaoId = l.GetInt64(1),
        PedidoId = l.GetInt64(2),
        NumeroPedido = l.GetInt64(3),
        Caixa = l.GetInt32(4),
        Forma = (FormaPagamento)l.GetInt32(5),
        ValorCentavos = l.GetInt64(6),
        Motivo = l.GetString(7),
        CriadoEm = Banco.LerData(l, "criado_em"),
    };
}
