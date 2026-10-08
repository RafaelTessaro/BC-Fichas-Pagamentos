using BCFichas.Core.Dados;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>Grava pedidos, confirma pagamentos e baixa o estoque.</summary>
public sealed class VendaServico
{
    private const string ColunasPedido =
        "id, numero, sessao_id, caixa, criado_em, total, forma, recebido, troco, status, autorizacao, impressoes, teste, fichas_saidas";

    private readonly Banco _banco;
    private readonly Func<DateTime> _agora;

    public VendaServico(Banco banco, Func<DateTime>? agora = null)
    {
        _banco = banco;
        _agora = agora ?? (() => DateTime.Now);
    }

    /// <summary>Estoque mudou (venda confirmada, ficha devolvida).</summary>
    public event Action? EstoqueAlterado;

    internal void AvisarEstoque() => EstoqueAlterado?.Invoke();

    /// <summary>
    /// Grava o pedido. Em dinheiro ele já sai pago; nas outras formas fica aguardando a maquininha,
    /// assim, se o programa fechar no meio do pagamento, o pedido não se perde.
    /// No modo teste a numeração é separada (começa do 1) e o estoque não é baixado.
    /// </summary>
    public Pedido CriarPedido(SessaoCaixa sessao, IReadOnlyList<LinhaCarrinho> linhas, FormaPagamento forma,
        long recebidoCentavos = 0)
    {
        if (!sessao.Aberta) throw new ErroDeNegocio("O caixa está fechado. Abra o caixa para vender.");
        if (linhas.Count == 0 || linhas.All(l => l.Quantidade <= 0)) throw new ErroDeNegocio("O pedido está vazio.");

        // Só as linhas que viram itens do pedido (quantidade zero ou negativa fica de fora do total também)
        var total = linhas.Where(l => l.Quantidade > 0).Sum(l => l.TotalCentavos);
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

        var baixou = false;
        var pedido = _banco.Transacao((c, t) =>
        {
            var combos = ConferirEstoque(c, t, linhas);

            var numero = Banco.Escalar<long>(c, t,
                "UPDATE contadores SET valor = valor + 1 WHERE nome = $n RETURNING valor",
                ("$n", sessao.Teste ? "pedido_teste" : "pedido"));
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
                Teste = sessao.Teste,
            };
            p.Id = Banco.Escalar<long>(c, t, """
                INSERT INTO pedidos (numero, sessao_id, caixa, criado_em, total, forma, recebido, troco, status, teste)
                VALUES ($n, $s, $c, $d, $t, $f, $r, $tr, $st, $te) RETURNING id
                """,
                ("$n", p.Numero), ("$s", p.SessaoId), ("$c", p.Caixa), ("$d", Banco.Data(p.CriadoEm)),
                ("$t", p.TotalCentavos), ("$f", (int)p.Forma), ("$r", p.RecebidoCentavos), ("$tr", p.TrocoCentavos),
                ("$st", (int)p.Status), ("$te", p.Teste ? 1 : 0));

            foreach (var linha in linhas.Where(l => l.Quantidade > 0))
            {
                // Combo: as fichas são as do combo como está agora no cadastro (guardadas junto com a venda).
                var componentes = combos[linha.Produto.Id];
                var item = new ItemPedido
                {
                    PedidoId = p.Id,
                    ProdutoId = linha.Produto.Id,
                    Nome = linha.Produto.Nome,
                    Detalhe = linha.Produto.Detalhe,
                    PrecoCentavos = linha.Produto.PrecoCentavos,
                    Quantidade = linha.Quantidade,
                    FichasPorUnidade = componentes.Count > 0
                        ? componentes.Sum(x => x.Quantidade)
                        : Math.Max(1, linha.Produto.FichasPorUnidade),
                };
                item.Id = Banco.Escalar<long>(c, t, """
                    INSERT INTO itens_pedido (pedido_id, produto_id, nome, detalhe, preco, quantidade, fichas_por_unidade)
                    VALUES ($p, $pr, $n, $d, $v, $q, $f) RETURNING id
                    """,
                    ("$p", item.PedidoId), ("$pr", item.ProdutoId), ("$n", item.Nome), ("$d", item.Detalhe),
                    ("$v", item.PrecoCentavos), ("$q", item.Quantidade), ("$f", item.FichasPorUnidade));
                for (var i = 0; i < componentes.Count; i++)
                {
                    var x = componentes[i];
                    var componente = new ComponenteItem
                    {
                        ProdutoId = x.ProdutoId,
                        Nome = x.Nome,
                        Detalhe = x.Detalhe,
                        ValorCentavos = x.ValorCentavos,
                        Quantidade = x.Quantidade,
                    };
                    componente.Id = Banco.Escalar<long>(c, t, """
                        INSERT INTO componentes_item (item_id, produto_id, nome, detalhe, valor, quantidade, ordem)
                        VALUES ($i, $p, $n, $d, $v, $q, $o) RETURNING id
                        """,
                        ("$i", item.Id), ("$p", componente.ProdutoId), ("$n", componente.Nome), ("$d", componente.Detalhe),
                        ("$v", componente.ValorCentavos), ("$q", componente.Quantidade), ("$o", i + 1));
                    item.Componentes.Add(componente);
                }
                p.Itens.Add(item);
            }

            if (p.Status == StatusPedido.Pago && !p.Teste) baixou = BaixarEstoque(c, t, p.Itens);
            return p;
        });

        // Só avisa a tela de venda quando algum estoque mudou de verdade (ela atualiza o "restam X" dos botões)
        if (baixou) EstoqueAlterado?.Invoke();
        return pedido;
    }

    public Pedido ConfirmarPagamento(long pedidoId, string? autorizacao)
    {
        var baixou = false;
        var pedido = _banco.Transacao((c, t) =>
        {
            var p = Ler(c, t, pedidoId) ?? throw new ErroDeNegocio("Pedido não encontrado.");
            if (p.Status == StatusPedido.Pago) return p;
            if (p.Status == StatusPedido.Cancelado) throw new ErroDeNegocio("Este pedido foi cancelado.");

            Banco.Executar(c, t, "UPDATE pedidos SET status = $st, autorizacao = $a WHERE id = $id",
                ("$st", (int)StatusPedido.Pago), ("$a", autorizacao), ("$id", pedidoId));
            if (!p.Teste) baixou = BaixarEstoque(c, t, p.Itens);
            p.Status = StatusPedido.Pago;
            p.Autorizacao = autorizacao;
            return p;
        });
        if (baixou) EstoqueAlterado?.Invoke();
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
        _banco.Executar("UPDATE pedidos SET impressoes = impressoes + 1, fichas_saidas = 0 WHERE id = $id", ("$id", pedidoId));

    /// <summary>
    /// A impressão parou no meio (porta COM sem papel): as fichas até a <paramref name="ate"/> já saíram e estão com
    /// o cliente. A próxima impressão do pedido manda só as que faltam.
    /// </summary>
    public void RegistrarFichasSaidas(long pedidoId, int ate) =>
        _banco.Executar("UPDATE pedidos SET fichas_saidas = $a WHERE id = $id", ("$a", ate), ("$id", pedidoId));

    public Pedido? Pedido(long id)
    {
        using var c = _banco.Abrir();
        return Ler(c, null, id);
    }

    /// <summary>Pedidos de um caixa, do mais recente para o mais antigo (sem os itens).</summary>
    /// <param name="soNaoImpressos">Só os pagos cujas fichas ainda não saíram (a impressora falhou).</param>
    public List<Pedido> Pedidos(long sessaoId, long? numero = null, int limite = 300, bool soNaoImpressos = false)
    {
        var sql = $"SELECT {ColunasPedido} FROM pedidos WHERE sessao_id = $s";
        if (numero is not null) sql += " AND numero = $n";
        if (soNaoImpressos) sql += " AND status = $pago AND impressoes = 0";
        sql += " ORDER BY id DESC LIMIT $l";
        return _banco.Consultar(sql, LerPedido, ("$s", sessaoId), ("$n", numero), ("$l", limite),
            ("$pago", (int)StatusPedido.Pago));
    }

    /// <summary>Pedidos pagos deste caixa cujas fichas ainda não saíram (a impressora falhou na hora).</summary>
    public int NaoImpressos(long sessaoId) => (int)_banco.Escalar<long>(
        "SELECT COUNT(*) FROM pedidos WHERE sessao_id = $s AND status = $pago AND impressoes = 0",
        ("$s", sessaoId), ("$pago", (int)StatusPedido.Pago));

    /// <summary>
    /// Pedido pelo número impresso na ficha ("PED: 13"), com os itens. Os números do modo teste são
    /// separados dos de verdade.
    /// </summary>
    public Pedido? PedidoPorNumero(long numero, bool teste = false)
    {
        using var c = _banco.Abrir();
        var id = Banco.Escalar<long?>(c, null,
            "SELECT id FROM pedidos WHERE numero = $n AND teste = $t ORDER BY id DESC LIMIT 1",
            ("$n", numero), ("$t", teste ? 1 : 0));
        return id is null ? null : Ler(c, null, id.Value);
    }

    /// <summary>
    /// Pedidos que ficaram esperando a maquininha. O status vai escrito na consulta (e não como parâmetro) para o
    /// banco usar o índice ix_pedidos_pendentes, que só tem esses pedidos, em vez de ler todos os pedidos guardados.
    /// </summary>
    public List<Pedido> Pendentes() =>
        _banco.Consultar(
            $"SELECT {ColunasPedido} FROM pedidos WHERE status = {(int)StatusPedido.AguardandoPagamento} ORDER BY id",
            LerPedido);

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

    /// <summary>
    /// Confere se os produtos existem, estão à venda e têm estoque — somando o que os combos usam (um combo
    /// de 5 HEINEKEN gasta 5 do estoque da HEINEKEN). Devolve as fichas de cada combo.
    /// </summary>
    private static Dictionary<long, List<ComponenteCombo>> ConferirEstoque(SqliteConnection c, SqliteTransaction t,
        IReadOnlyList<LinhaCarrinho> linhas)
    {
        var combos = new Dictionary<long, List<ComponenteCombo>>();
        var necessario = new Dictionary<long, int>();
        void Precisa(long produtoId, int quantidade) =>
            necessario[produtoId] = necessario.GetValueOrDefault(produtoId) + quantidade;

        foreach (var linha in linhas.Where(l => l.Quantidade > 0))
        {
            var atual = Banco.Consultar(c, t, "SELECT ativo FROM produtos WHERE id = $id",
                l => l.GetInt64(0) != 0, ("$id", linha.Produto.Id)).Cast<bool?>().FirstOrDefault();
            if (atual is null)
                throw new ErroDeNegocio($"O produto {linha.Produto.Nome} foi excluído. Tire ele do pedido.");
            if (atual == false)
                throw new ErroDeNegocio($"O produto {linha.Produto.Nome} está inativo. Tire ele do pedido.");

            var componentes = CatalogoServico.Componentes(c, t, linha.Produto.Id);
            combos[linha.Produto.Id] = componentes;
            Precisa(linha.Produto.Id, linha.Quantidade);
            foreach (var componente in componentes.Where(x => x.ProdutoId is not null))
                Precisa(componente.ProdutoId!.Value, linha.Quantidade * componente.Quantidade);
        }

        foreach (var (produtoId, quantidade) in necessario)
        {
            var estoque = Banco.Consultar(c, t, "SELECT controla_estoque, estoque, nome FROM produtos WHERE id = $id",
                l => (Controla: l.GetInt64(0) != 0, Estoque: l.GetInt32(1), Nome: l.GetString(2)), ("$id", produtoId))
                .FirstOrDefault();
            if (estoque.Controla && estoque.Estoque < quantidade)
                throw new ErroDeNegocio(estoque.Estoque <= 0
                    ? $"{estoque.Nome} está esgotado."
                    : $"Só restam {estoque.Estoque} de {estoque.Nome}.");
        }
        return combos;
    }

    /// <summary>
    /// Baixa o estoque do produto vendido e, nos combos, dos produtos das fichas. Guarda junto quanto saiu de
    /// verdade (o estoque não fica negativo e produto sem controle de estoque não baixa), para "Apagar as vendas"
    /// devolver exatamente isso.
    /// </summary>
    /// <returns>Se algum estoque mudou (produto sem controle de estoque não muda nada).</returns>
    private static bool BaixarEstoque(SqliteConnection c, SqliteTransaction t, IEnumerable<ItemPedido> itens)
    {
        var baixou = false;
        foreach (var item in itens)
        {
            if (item.ProdutoId is not null && Baixar(c, t, item.ProdutoId.Value, item.Quantidade) is var sai and > 0)
            {
                Banco.Executar(c, t, "UPDATE itens_pedido SET baixado = $b WHERE id = $id", ("$b", sai), ("$id", item.Id));
                baixou = true;
            }
            foreach (var componente in item.Componentes.Where(x => x.ProdutoId is not null))
            {
                if (Baixar(c, t, componente.ProdutoId!.Value, item.Quantidade * componente.Quantidade) is not (var saiu and > 0))
                    continue;
                Banco.Executar(c, t, "UPDATE componentes_item SET baixado = $b WHERE id = $id",
                    ("$b", saiu), ("$id", componente.Id));
                baixou = true;
            }
        }
        return baixou;

        static long Baixar(SqliteConnection c, SqliteTransaction t, long produtoId, int quantidade)
        {
            var sai = Banco.Escalar<long?>(c, t,
                "SELECT MIN(MAX(estoque, 0), $q) FROM produtos WHERE id = $id AND controla_estoque = 1",
                ("$q", quantidade), ("$id", produtoId)) ?? 0;
            if (sai > 0)
                Banco.Executar(c, t, "UPDATE produtos SET estoque = estoque - $q WHERE id = $id",
                    ("$q", sai), ("$id", produtoId));
            return sai;
        }
    }

    private static Pedido? Ler(SqliteConnection c, SqliteTransaction? t, long id)
    {
        var pedido = Banco.Consultar(c, t, $"SELECT {ColunasPedido} FROM pedidos WHERE id = $id", LerPedido, ("$id", id))
            .FirstOrDefault();
        if (pedido is null) return null;
        pedido.Itens = Banco.Consultar(c, t, """
            SELECT i.id, i.pedido_id, i.produto_id, i.nome, i.detalhe, i.preco, i.quantidade, i.fichas_por_unidade,
                   (SELECT COALESCE(SUM(d.quantidade), 0) FROM itens_devolucao d
                    WHERE d.item_id = i.id AND d.componente_id IS NULL)
            FROM itens_pedido i WHERE i.pedido_id = $p ORDER BY i.id
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
            Devolvidas = l.GetInt32(8),
        }, ("$p", id));

        var componentes = Banco.Consultar(c, t, """
            SELECT ci.item_id, ci.id, ci.produto_id, ci.nome, ci.detalhe, ci.valor, ci.quantidade,
                   (SELECT COALESCE(SUM(d.quantidade), 0) FROM itens_devolucao d WHERE d.componente_id = ci.id)
            FROM componentes_item ci JOIN itens_pedido i ON i.id = ci.item_id
            WHERE i.pedido_id = $p ORDER BY ci.item_id, ci.ordem, ci.id
            """, l => (Item: l.GetInt64(0), Componente: new ComponenteItem
        {
            Id = l.GetInt64(1),
            ProdutoId = l.IsDBNull(2) ? null : l.GetInt64(2),
            Nome = l.GetString(3),
            Detalhe = l.GetString(4),
            ValorCentavos = l.GetInt64(5),
            Quantidade = l.GetInt32(6),
            Devolvidas = l.GetInt32(7),
        }), ("$p", id)).ToLookup(x => x.Item, x => x.Componente);
        foreach (var item in pedido.Itens) item.Componentes = componentes[item.Id].ToList();
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
        Teste = l.GetInt64(12) != 0,
        FichasSaidas = l.GetInt32(13),
    };
}
