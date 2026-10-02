using BCFichas.Core.Dados;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>Abertura, sangria/suprimento, resumo e fechamento do caixa.</summary>
public sealed class CaixaServico
{
    private const string ColunasSessao = "id, caixa, operador, aberta_em, valor_abertura, fechada_em, valor_contado";
    private const string ColunasMovimento = "id, sessao_id, caixa, tipo, valor, motivo, usuario, criado_em";

    private readonly Banco _banco;
    private readonly Func<DateTime> _agora;

    public CaixaServico(Banco banco, Func<DateTime>? agora = null)
    {
        _banco = banco;
        _agora = agora ?? (() => DateTime.Now);
    }

    public SessaoCaixa? SessaoAberta(int caixa) =>
        _banco.Consultar($"SELECT {ColunasSessao} FROM sessoes WHERE caixa = $c AND fechada_em IS NULL ORDER BY id DESC LIMIT 1",
            LerSessao, ("$c", caixa)).FirstOrDefault();

    public SessaoCaixa? Sessao(long id) =>
        _banco.Consultar($"SELECT {ColunasSessao} FROM sessoes WHERE id = $id", LerSessao, ("$id", id)).FirstOrDefault();

    /// <param name="operador">Opcional: os caixas são identificados só pelo número (sessões antigas têm o nome).</param>
    public SessaoCaixa Abrir(int caixa, string? operador, long valorAbertura)
    {
        operador = (operador ?? "").Trim().ToUpperInvariant();
        if (valorAbertura < 0) throw new ErroDeNegocio("O valor de abertura não pode ser negativo.");
        if (SessaoAberta(caixa) is not null) throw new ErroDeNegocio($"O caixa {caixa:00} já está aberto.");

        var sessao = new SessaoCaixa
        {
            Caixa = caixa,
            Operador = operador,
            AbertaEm = _agora(),
            ValorAberturaCentavos = valorAbertura,
        };
        sessao.Id = _banco.Escalar<long>(
            "INSERT INTO sessoes (caixa, operador, aberta_em, valor_abertura) VALUES ($c, $o, $a, $v) RETURNING id",
            ("$c", caixa), ("$o", operador), ("$a", Banco.Data(sessao.AbertaEm)), ("$v", valorAbertura));
        return sessao;
    }

    public Movimento RegistrarMovimento(SessaoCaixa sessao, TipoMovimento tipo, long valor, string motivo)
    {
        if (!sessao.Aberta) throw new ErroDeNegocio("O caixa está fechado.");
        if (valor <= 0) throw new ErroDeNegocio("Informe um valor maior que zero.");

        if (tipo == TipoMovimento.Sangria)
        {
            var disponivel = Resumo(sessao.Id).DinheiroEsperado;
            if (valor > disponivel)
                throw new ErroDeNegocio($"Não dá para retirar {Dinheiro.Formatar(valor)}: no caixa há {Dinheiro.Formatar(disponivel)} em dinheiro.");
        }

        var movimento = new Movimento
        {
            SessaoId = sessao.Id,
            Caixa = sessao.Caixa,
            Tipo = tipo,
            ValorCentavos = valor,
            Motivo = (motivo ?? "").Trim().ToUpperInvariant(),
            Usuario = sessao.Operador,
            CriadoEm = _agora(),
        };
        movimento.Id = _banco.Escalar<long>("""
            INSERT INTO movimentos (sessao_id, caixa, tipo, valor, motivo, usuario, criado_em)
            VALUES ($s, $c, $t, $v, $m, $u, $d) RETURNING id
            """,
            ("$s", movimento.SessaoId), ("$c", movimento.Caixa), ("$t", (int)tipo), ("$v", valor),
            ("$m", movimento.Motivo), ("$u", movimento.Usuario), ("$d", Banco.Data(movimento.CriadoEm)));
        return movimento;
    }

    public List<Movimento> Movimentos(long sessaoId) =>
        _banco.Consultar($"SELECT {ColunasMovimento} FROM movimentos WHERE sessao_id = $s ORDER BY id DESC",
            LerMovimento, ("$s", sessaoId));

    public List<Movimento> Movimentos(DateTime de, DateTime ate) =>
        _banco.Consultar($"SELECT {ColunasMovimento} FROM movimentos WHERE criado_em >= $de AND criado_em < $ate ORDER BY id DESC",
            LerMovimento, ("$de", Banco.Data(de.Date)), ("$ate", Banco.Data(ate.Date.AddDays(1))));

    public ResumoCaixa Resumo(long sessaoId)
    {
        var sessao = Sessao(sessaoId) ?? throw new ErroDeNegocio("Caixa não encontrado.");

        var porForma = new Dictionary<FormaPagamento, long>();
        var pedidosPorForma = new Dictionary<FormaPagamento, int>();
        foreach (var (forma, total, qtd) in _banco.Consultar(
                     "SELECT forma, SUM(total), COUNT(*) FROM pedidos WHERE sessao_id = $s AND status = $pago GROUP BY forma",
                     l => ((FormaPagamento)l.GetInt32(0), l.GetInt64(1), l.GetInt32(2)),
                     ("$s", sessaoId), ("$pago", (int)StatusPedido.Pago)))
        {
            porForma[forma] = total;
            pedidosPorForma[forma] = qtd;
        }

        var produtos = _banco.Consultar("""
            SELECT i.nome, SUM(i.quantidade), SUM(i.quantidade * i.preco)
            FROM itens_pedido i JOIN pedidos p ON p.id = i.pedido_id
            WHERE p.sessao_id = $s AND p.status = $pago
            GROUP BY i.nome ORDER BY SUM(i.quantidade) DESC, i.nome
            """,
            l => new ProdutoVendido(l.GetString(0), l.GetInt32(1), l.GetInt64(2)),
            ("$s", sessaoId), ("$pago", (int)StatusPedido.Pago));

        var fichas = _banco.Escalar<long?>("""
            SELECT SUM(i.quantidade * i.fichas_por_unidade)
            FROM itens_pedido i JOIN pedidos p ON p.id = i.pedido_id
            WHERE p.sessao_id = $s AND p.status = $pago
            """, ("$s", sessaoId), ("$pago", (int)StatusPedido.Pago)) ?? 0;

        long Soma(TipoMovimento tipo) => _banco.Escalar<long?>(
            "SELECT SUM(valor) FROM movimentos WHERE sessao_id = $s AND tipo = $t",
            ("$s", sessaoId), ("$t", (int)tipo)) ?? 0;

        return new ResumoCaixa
        {
            Sessao = sessao,
            PorForma = porForma,
            PedidosPorForma = pedidosPorForma,
            QuantidadePedidos = pedidosPorForma.Values.Sum(),
            QuantidadeFichas = (int)fichas,
            Sangrias = Soma(TipoMovimento.Sangria),
            Suprimentos = Soma(TipoMovimento.Suprimento),
            Produtos = produtos,
        };
    }

    public ResumoCaixa Fechar(SessaoCaixa sessao, long? valorContado)
    {
        if (!sessao.Aberta) throw new ErroDeNegocio("Este caixa já foi fechado.");
        var pendentes = _banco.Escalar<long>("SELECT COUNT(*) FROM pedidos WHERE sessao_id = $s AND status = $st",
            ("$s", sessao.Id), ("$st", (int)StatusPedido.AguardandoPagamento));
        if (pendentes > 0)
            throw new ErroDeNegocio("Há pedido aguardando pagamento na maquininha. Termine ou cancele antes de fechar o caixa.");

        sessao.FechadaEm = _agora();
        sessao.ValorContadoCentavos = valorContado;
        _banco.Executar("UPDATE sessoes SET fechada_em = $f, valor_contado = $v WHERE id = $id",
            ("$f", Banco.Data(sessao.FechadaEm.Value)), ("$v", valorContado), ("$id", sessao.Id));
        return Resumo(sessao.Id);
    }

    public List<SessaoCaixa> Sessoes(DateTime de, DateTime ate) =>
        _banco.Consultar($"SELECT {ColunasSessao} FROM sessoes WHERE aberta_em >= $de AND aberta_em < $ate ORDER BY id DESC",
            LerSessao, ("$de", Banco.Data(de.Date)), ("$ate", Banco.Data(ate.Date.AddDays(1))));

    private static SessaoCaixa LerSessao(SqliteDataReader l) => new()
    {
        Id = l.GetInt64(0),
        Caixa = l.GetInt32(1),
        Operador = l.GetString(2),
        AbertaEm = Banco.LerData(l, "aberta_em"),
        ValorAberturaCentavos = l.GetInt64(4),
        FechadaEm = Banco.LerDataOpcional(l, "fechada_em"),
        ValorContadoCentavos = Banco.LerLongOpcional(l, "valor_contado"),
    };

    private static Movimento LerMovimento(SqliteDataReader l) => new()
    {
        Id = l.GetInt64(0),
        SessaoId = l.GetInt64(1),
        Caixa = l.GetInt32(2),
        Tipo = (TipoMovimento)l.GetInt32(3),
        ValorCentavos = l.GetInt64(4),
        Motivo = l.GetString(5),
        Usuario = l.GetString(6),
        CriadoEm = Banco.LerData(l, "criado_em"),
    };
}
