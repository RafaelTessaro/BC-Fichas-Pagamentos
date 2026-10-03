using BCFichas.Core;
using BCFichas.Core.Dados;

namespace BCFichas.Tests.Core;

/// <summary>Enche o banco com muitas vendas de uma vez (para medir memória e tempo com o banco cheio).</summary>
public static class MuitasVendas
{
    /// <summary>
    /// Cria <paramref name="dias"/> caixas fechados (um por dia, terminando ontem) com <paramref name="pedidosPorDia"/>
    /// pedidos cada, de 1 a 4 itens, nas 4 formas de pagamento. Grava direto no banco numa transação só.
    /// </summary>
    public static int Criar(Sistema sistema, int dias, int pedidosPorDia, int semente = 7)
    {
        var aleatorio = new Random(semente);
        var produtos = sistema.Catalogo.Produtos();
        var formas = Enum.GetValues<FormaPagamento>();
        var total = 0;

        using var conexao = sistema.Banco.Abrir();
        using var transacao = conexao.BeginTransaction();
        using var pedido = conexao.CreateCommand();
        pedido.Transaction = transacao;
        pedido.CommandText = """
            INSERT INTO pedidos (numero, sessao_id, caixa, criado_em, total, forma, recebido, troco, status, impressoes)
            VALUES ($n, $s, 1, $d, $t, $f, $t, 0, 1, 1) RETURNING id
            """;
        var pNumero = pedido.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pSessao = pedido.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pData = pedido.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text);
        var pTotal = pedido.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pForma = pedido.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Integer);

        using var item = conexao.CreateCommand();
        item.Transaction = transacao;
        item.CommandText = """
            INSERT INTO itens_pedido (pedido_id, produto_id, nome, preco, quantidade, fichas_por_unidade)
            VALUES ($p, $pr, $n, $v, $q, 1)
            """;
        var iPedido = item.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Integer);
        var iProduto = item.Parameters.Add("$pr", Microsoft.Data.Sqlite.SqliteType.Integer);
        var iNome = item.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Text);
        var iPreco = item.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Integer);
        var iQtd = item.Parameters.Add("$q", Microsoft.Data.Sqlite.SqliteType.Integer);

        var numero = Banco.Escalar<long>(conexao, transacao, "SELECT valor FROM contadores WHERE nome = 'pedido'");
        for (var dia = dias; dia >= 1; dia--)
        {
            var abertura = DateTime.Today.AddDays(-dia).AddHours(17);
            var sessao = Banco.Escalar<long>(conexao, transacao, """
                INSERT INTO sessoes (caixa, operador, aberta_em, valor_abertura, fechada_em, valor_contado)
                VALUES (1, '', $a, 10000, $f, NULL) RETURNING id
                """, ("$a", Banco.Data(abertura)), ("$f", Banco.Data(abertura.AddHours(8))));

            for (var i = 0; i < pedidosPorDia; i++)
            {
                var itens = Enumerable.Range(0, aleatorio.Next(1, 5))
                    .Select(_ => (Produto: produtos[aleatorio.Next(produtos.Count)], Qtd: aleatorio.Next(1, 4)))
                    .ToList();
                pNumero.Value = ++numero;
                pSessao.Value = sessao;
                pData.Value = Banco.Data(abertura.AddSeconds(i * 28800.0 / pedidosPorDia));
                pTotal.Value = itens.Sum(x => x.Produto.PrecoCentavos * x.Qtd);
                pForma.Value = (int)formas[aleatorio.Next(formas.Length)];
                var id = (long)pedido.ExecuteScalar()!;
                foreach (var (produto, qtd) in itens)
                {
                    iPedido.Value = id;
                    iProduto.Value = produto.Id;
                    iNome.Value = produto.Nome;
                    iPreco.Value = produto.PrecoCentavos;
                    iQtd.Value = qtd;
                    item.ExecuteNonQuery();
                }
                total++;
            }
        }
        Banco.Executar(conexao, transacao, "UPDATE contadores SET valor = $v WHERE nome = 'pedido'", ("$v", numero));
        transacao.Commit();
        return total;
    }
}
