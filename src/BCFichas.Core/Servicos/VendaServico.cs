using BCFichas.Core.Dados;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>Grava pedidos, confirma pagamentos e baixa o estoque.</summary>
public sealed class VendaServico
{
    private const string ColunasPedido =
        "id, numero, sessao_id, caixa, criado_em, total, forma, recebido, troco, status, autorizacao, impressoes";

    private readonly Banco _banco;
    private readonly Func<DateTime> _agora;

    public VendaServico(Banco banco, Func<DateTime>? agora = null)
    {
        _banco = banco;
        _agora = agora ?? (() => DateTime.Now);
    }

    /// <summary>Estoque mudou (venda confirmada).</summary>
    public event Action? EstoqueAlterado;

    /// <summary>
    /// Grava o pedido. Em dinheiro ele já sai pago; nas outras formas fica aguardando a maquininha,
    /// assim, se o programa fechar no meio do pagamento, o pedido não se perde.
    /// </summary>
    public Pedido CriarPedido(SessaoCaixa sessao, IReadOnlyList<LinhaCarrinho> linhas, FormaPagamento forma,
        long recebidoCentavos = 0)
    {
        if (!sessao.Aberta) throw new ErroDeNegocio("O caixa está fechado. Abra o caixa para vender.");
        if (linhas.Count == 0 || linhas.All(l => l.Quantidade <= 0)) throw new ErroDeNegocio("O pedido está vazio.");

        var total = linhas.Sum(l => l.TotalCentavos);
        long troco = 0;
        if (forma == FormaPagamento.Dinheiro)
        {
            if (recebidoCentavos < total)
                throw new ErroDeNegocio($"Valor recebido ({Dinheiro.Formatar(recebidoCentavos)}) é menor que o total ({Dinheiro.Formatar(total)}).");
            troco = recebidoCentavos - total;
        }
        else
        {
            recebidoCentavos = total;
        }

        var pedido = _banco.Transacao((c, t) =>
        {
            ConferirEstoque(c, t, linhas);

            var numero = Banco.Escalar<long>(c, t,
                "UPDATE contadores SET valor = valor + 1 WHERE nome = 'pedido' RETURNING valor");
            var p = new Pedido
            {
                Numero = numero,
                SessaoId = sessao.Id,
                Caixa = sessao.Caixa,
                CriadoEm = _agora(),
                TotalCentavos = total,
                Forma = forma,
                RecebidoCentavos = recebidoCentavos,
                TrocoCentavos = troco,
                Status = forma == FormaPagamento.Dinheiro ? StatusPedido.Pago : StatusPedido.AguardandoPagamento,
            };
            p.Id = Banco.Escalar<long>(c, t, """
                INSERT INTO pedidos (numero, sessao_id, caixa, criado_em, total, forma, recebido, troco, status)
                VALUES ($n, $s, $c, $d, $t, $f, $r, $tr, $st) RETURNING id
                """,
                ("$n", p.Numero), ("$s", p.SessaoId), ("$c", p.Caixa), ("$d", Banco.Data(p.CriadoEm)),
                ("$t", p.TotalCentavos), ("$f", (int)p.Forma), ("$r", p.RecebidoCentavos), ("$tr", p.TrocoCentavos),
                ("$st", (int)p.Status));

            foreach (var linha in linhas.Where(l => l.Quantidade > 0))
            {
                var item = new ItemPedido
                {
                    PedidoId = p.Id,
                    ProdutoId = linha.Produto.Id,
                    Nome = linha.Produto.Nome,
                    Detalhe = linha.Produto.Detalhe,
                    PrecoCentavos = linha.Produto.PrecoCentavos,
                    Quantidade = linha.Quantidade,
                    FichasPorUnidade = Math.Max(1, linha.Produto.FichasPorUnidade),
                };
                item.Id = Banco.Escalar<long>(c, t, """
                    INSERT INTO itens_pedido (pedido_id, produto_id, nome, detalhe, preco, quantidade, fichas_por_unidade)
                    VALUES ($p, $pr, $n, $d, $v, $q, $f) RETURNING id
                    """,
                    ("$p", item.PedidoId), ("$pr", item.ProdutoId), ("$n", item.Nome), ("$d", item.Detalhe),
                    ("$v", item.PrecoCentavos), ("$q", item.Quantidade), ("$f", item.FichasPorUnidade));
                p.Itens.Add(item);
            }

            if (p.Status == StatusPedido.Pago) BaixarEstoque(c, t, p.Itens);
            return p;
        });

        if (pedido.Status == StatusPedido.Pago) EstoqueAlterado?.Invoke();
        return pedido;
    }

    public Pedido ConfirmarPagamento(long pedidoId, string? autorizacao)
    {
        var pedido = _banco.Transacao((c, t) =>
        {
            var p = Ler(c, t, pedidoId) ?? throw new ErroDeNegocio("Pedido não encontrado.");
            if (p.Status == StatusPedido.Pago) return p;
            if (p.Status == StatusPedido.Cancelado) throw new ErroDeNegocio("Este pedido foi cancelado.");

            Banco.Executar(c, t, "UPDATE pedidos SET status = $st, autorizacao = $a WHERE id = $id",
                ("$st", (int)StatusPedido.Pago), ("$a", autorizacao), ("$id", pedidoId));
            BaixarEstoque(c, t, p.Itens);
            p.Status = StatusPedido.Pago;
            p.Autorizacao = autorizacao;
            return p;
        });
        EstoqueAlterado?.Invoke();
        return pedido;
    }

    /// <summary>Cancela um pedido que ainda não foi pago (cartão recusado, desistência).</summary>
    public void Cancelar(long pedidoId)
    {
        var alterados = _banco.Executar("UPDATE pedidos SET status = $c WHERE id = $id AND status = $ag",
            ("$c", (int)StatusPedido.Cancelado), ("$id", pedidoId), ("$ag", (int)StatusPedido.AguardandoPagamento));
        if (alterados == 0)
        {
            var p = Pedido(pedidoId);
            if (p?.Status == StatusPedido.Pago)
                throw new ErroDeNegocio("Pedido já pago não pode ser cancelado por aqui.");
        }
    }

    public void RegistrarImpressao(long pedidoId) =>
        _banco.Executar("UPDATE pedidos SET impressoes = impressoes + 1 WHERE id = $id", ("$id", pedidoId));

    public Pedido? Pedido(long id)
    {
        using var c = _banco.Abrir();
        return Ler(c, null, id);
    }

    /// <summary>Pedidos de um caixa, do mais recente para o mais antigo (sem os itens).</summary>
    public List<Pedido> Pedidos(long sessaoId, long? numero = null, int limite = 300)
    {
        var sql = $"SELECT {ColunasPedido} FROM pedidos WHERE sessao_id = $s";
        if (numero is not null) sql += " AND numero = $n";
        sql += " ORDER BY id DESC LIMIT $l";
        return _banco.Consultar(sql, LerPedido, ("$s", sessaoId), ("$n", numero), ("$l", limite));
    }

    public List<Pedido> Pendentes() =>
        _banco.Consultar($"SELECT {ColunasPedido} FROM pedidos WHERE status = $st ORDER BY id",
            LerPedido, ("$st", (int)StatusPedido.AguardandoPagamento));

    /// <summary>
    /// Ao abrir o programa: pergunta à maquininha o que aconteceu com pedidos que ficaram no meio do pagamento.
    /// Retorna os pedidos que estavam pagos (para reimprimir as fichas).
    /// </summary>
    public async Task<List<Pedido>> ResolverPendentesAsync(IMaquininha maquininha, CancellationToken cancelar)
    {
        var pagos = new List<Pedido>();
        foreach (var pendente in Pendentes())
        {
            ResultadoCobranca? resultado = null;
            try
            {
                resultado = await maquininha.ConsultarAsync(IdCobranca(pendente), cancelar);
            }
            catch (Exception) when (!cancelar.IsCancellationRequested)
            {
                // Sem resposta da maquininha: deixa como cancelado; o operador confere no extrato.
            }

            if (resultado?.Aprovado == true)
                pagos.Add(ConfirmarPagamento(pendente.Id, resultado.Autorizacao));
            else
                Cancelar(pendente.Id);
        }
        return pagos;
    }

    public static string IdCobranca(Pedido pedido) => $"C{pedido.Caixa:00}-P{pedido.Numero:000000}-{pedido.Id}";

    private static void ConferirEstoque(SqliteConnection c, SqliteTransaction t, IReadOnlyList<LinhaCarrinho> linhas)
    {
        foreach (var linha in linhas.Where(l => l.Quantidade > 0))
        {
            var atual = Banco.Consultar(c, t, "SELECT controla_estoque, estoque, ativo FROM produtos WHERE id = $id",
                l => (Controla: l.GetInt64(0) != 0, Estoque: l.GetInt32(1), Ativo: l.GetInt64(2) != 0),
                ("$id", linha.Produto.Id)).FirstOrDefault();
            if (atual == default)
                throw new ErroDeNegocio($"O produto {linha.Produto.Nome} foi excluído. Tire ele do pedido.");
            if (!atual.Ativo)
                throw new ErroDeNegocio($"O produto {linha.Produto.Nome} está inativo. Tire ele do pedido.");
            if (atual.Controla && atual.Estoque < linha.Quantidade)
                throw new ErroDeNegocio(atual.Estoque <= 0
                    ? $"{linha.Produto.Nome} está esgotado."
                    : $"Só restam {atual.Estoque} de {linha.Produto.Nome}.");
        }
    }

    private static void BaixarEstoque(SqliteConnection c, SqliteTransaction t, IEnumerable<ItemPedido> itens)
    {
        foreach (var item in itens.Where(i => i.ProdutoId is not null))
        {
            Banco.Executar(c, t,
                "UPDATE produtos SET estoque = MAX(estoque - $q, 0) WHERE id = $id AND controla_estoque = 1",
                ("$q", item.Quantidade), ("$id", item.ProdutoId));
        }
    }

    private static Pedido? Ler(SqliteConnection c, SqliteTransaction? t, long id)
    {
        var pedido = Banco.Consultar(c, t, $"SELECT {ColunasPedido} FROM pedidos WHERE id = $id", LerPedido, ("$id", id))
            .FirstOrDefault();
        if (pedido is null) return null;
        pedido.Itens = Banco.Consultar(c, t, """
            SELECT id, pedido_id, produto_id, nome, detalhe, preco, quantidade, fichas_por_unidade
            FROM itens_pedido WHERE pedido_id = $p ORDER BY id
            """, l => new ItemPedido
        {
            Id = l.GetInt64(0),
            PedidoId = l.GetInt64(1),
            ProdutoId = l.IsDBNull(2) ? null : l.GetInt64(2),
            Nome = l.GetString(3),
            Detalhe = l.GetString(4),
            PrecoCentavos = l.GetInt64(5),
            Quantidade = l.GetInt32(6),
            FichasPorUnidade = l.GetInt32(7),
        }, ("$p", id));
        return pedido;
    }

    private static Pedido LerPedido(SqliteDataReader l) => new()
    {
        Id = l.GetInt64(0),
        Numero = l.GetInt64(1),
        SessaoId = l.GetInt64(2),
        Caixa = l.GetInt32(3),
        CriadoEm = Banco.LerData(l, "criado_em"),
        TotalCentavos = l.GetInt64(5),
        Forma = (FormaPagamento)l.GetInt32(6),
        RecebidoCentavos = l.GetInt64(7),
        TrocoCentavos = l.GetInt64(8),
        Status = (StatusPedido)l.GetInt32(9),
        Autorizacao = l.IsDBNull(10) ? null : l.GetString(10),
        Impressoes = l.GetInt32(11),
    };
}
