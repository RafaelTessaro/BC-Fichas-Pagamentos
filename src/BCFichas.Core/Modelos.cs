namespace BCFichas.Core;

public enum FormaPagamento
{
    Dinheiro = 1,
    Debito = 2,
    Credito = 3,
    Pix = 4,
}

public enum StatusPedido
{
    AguardandoPagamento = 0,
    Pago = 1,
    Cancelado = 2,
}

public enum TipoMovimento
{
    /// <summary>Retirada de dinheiro do caixa.</summary>
    Sangria = 1,
    /// <summary>Entrada de dinheiro no caixa (troco, reforço).</summary>
    Suprimento = 2,
}

public static class Nomes
{
    public static string De(FormaPagamento forma) => forma switch
    {
        FormaPagamento.Dinheiro => "Dinheiro",
        FormaPagamento.Debito => "Débito",
        FormaPagamento.Credito => "Crédito",
        FormaPagamento.Pix => "PIX",
        _ => forma.ToString(),
    };

    public static string De(StatusPedido status) => status switch
    {
        StatusPedido.AguardandoPagamento => "Aguardando pagamento",
        StatusPedido.Pago => "Pago",
        StatusPedido.Cancelado => "Cancelado",
        _ => status.ToString(),
    };

    public static string De(TipoMovimento tipo) => tipo switch
    {
        TipoMovimento.Sangria => "Sangria (retirada)",
        TipoMovimento.Suprimento => "Suprimento (entrada)",
        _ => tipo.ToString(),
    };
}

public sealed class Aba
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public int Ordem { get; set; }
}

public sealed class Produto
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public long AbaId { get; set; }
    /// <summary>Posição do botão na grade da aba, começando em 1.</summary>
    public int Posicao { get; set; } = 1;
    /// <summary>Quantas fichas imprimir para cada unidade vendida.</summary>
    public int FichasPorUnidade { get; set; } = 1;
    public long CustoCentavos { get; set; }
    public long PrecoCentavos { get; set; }
    public bool ControlaEstoque { get; set; }
    public int Estoque { get; set; }
    public string Cor { get; set; } = "#0E9F6E";
    public string? Imagem { get; set; }
    public bool Ativo { get; set; } = true;

    public bool Esgotado => ControlaEstoque && Estoque <= 0;
}

public sealed class SessaoCaixa
{
    public long Id { get; set; }
    public int Caixa { get; set; }
    public string Operador { get; set; } = "";
    public DateTime AbertaEm { get; set; }
    public long ValorAberturaCentavos { get; set; }
    public DateTime? FechadaEm { get; set; }
    public long? ValorContadoCentavos { get; set; }

    public bool Aberta => FechadaEm is null;
}

public sealed class Pedido
{
    public long Id { get; set; }
    public long Numero { get; set; }
    public long SessaoId { get; set; }
    public int Caixa { get; set; }
    public DateTime CriadoEm { get; set; }
    public long TotalCentavos { get; set; }
    public FormaPagamento Forma { get; set; }
    public long RecebidoCentavos { get; set; }
    public long TrocoCentavos { get; set; }
    public StatusPedido Status { get; set; }
    public string? Autorizacao { get; set; }
    public int Impressoes { get; set; }
    public List<ItemPedido> Itens { get; set; } = new();

    public int QuantidadeItens => Itens.Sum(i => i.Quantidade);
    public int QuantidadeFichas => Itens.Sum(i => i.Quantidade * i.FichasPorUnidade);
}

public sealed class ItemPedido
{
    public long Id { get; set; }
    public long PedidoId { get; set; }
    public long? ProdutoId { get; set; }
    public string Nome { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public long PrecoCentavos { get; set; }
    public int Quantidade { get; set; }
    public int FichasPorUnidade { get; set; } = 1;

    public long TotalCentavos => PrecoCentavos * Quantidade;
}

public sealed class Movimento
{
    public long Id { get; set; }
    public long SessaoId { get; set; }
    public int Caixa { get; set; }
    public TipoMovimento Tipo { get; set; }
    public long ValorCentavos { get; set; }
    public string Motivo { get; set; } = "";
    public string Usuario { get; set; } = "";
    public DateTime CriadoEm { get; set; }
}

public sealed record ProdutoVendido(string Nome, int Quantidade, long TotalCentavos);

public sealed class ResumoCaixa
{
    public required SessaoCaixa Sessao { get; init; }
    public Dictionary<FormaPagamento, long> PorForma { get; init; } = new();
    public Dictionary<FormaPagamento, int> PedidosPorForma { get; init; } = new();
    public int QuantidadePedidos { get; init; }
    public int QuantidadeFichas { get; init; }
    public long Sangrias { get; init; }
    public long Suprimentos { get; init; }
    public List<ProdutoVendido> Produtos { get; init; } = new();

    public long TotalVendas => PorForma.Values.Sum();
    public long Total(FormaPagamento forma) => PorForma.GetValueOrDefault(forma);

    /// <summary>Dinheiro que deveria estar na gaveta: abertura + vendas em dinheiro + suprimentos - sangrias.</summary>
    public long DinheiroEsperado =>
        Sessao.ValorAberturaCentavos + Total(FormaPagamento.Dinheiro) + Suprimentos - Sangrias;

    public long TicketMedio => QuantidadePedidos == 0 ? 0 : TotalVendas / QuantidadePedidos;
}

/// <summary>Uma ficha física a ser impressa.</summary>
public sealed class Ficha
{
    public string NomeEvento { get; init; } = "";
    public string Produto { get; init; } = "";
    public string Detalhe { get; init; } = "";
    public long PrecoCentavos { get; init; }
    public long NumeroPedido { get; init; }
    public int Caixa { get; init; }
    public DateTime Data { get; init; }
    public int Sequencia { get; init; }
    public int TotalFichas { get; init; }
    public string Rodape { get; init; } = "";
    public bool Reimpressao { get; init; }
    public FormaPagamento Forma { get; init; }

    /// <summary>Código único da ficha (usado no código de barras).</summary>
    public string Codigo => $"{Caixa:00}{NumeroPedido:000000}{Sequencia:000}";
}
