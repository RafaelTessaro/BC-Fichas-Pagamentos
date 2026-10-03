namespace BCFichas.Painel;

// O que o painel manda para o celular (JSON). Valores em centavos; datas no horário do tablet ("2026-10-03T21:14:05").

/// <summary>Identidade da máquina (usada para achar as outras na rede).</summary>
public sealed record Info(string Instancia, int Caixa, string NomeEvento, string Versao, bool Ativo);

public sealed record FormaVendida(string Chave, string Nome, long Valor, int Pedidos, long Devolvido);

public sealed record ProdutoVendidoPainel(string Nome, int Quantidade, long Valor);

/// <summary>Vendas de uma hora ("2026-10-03 18" = das 18h às 19h).</summary>
public sealed record HoraVendida(string Hora, int Pedidos, long Valor);

/// <summary>Números de um período: o caixa aberto agora ou o evento todo (todos os caixas desde a máquina pura).</summary>
public sealed record Bloco
{
    public long Vendido { get; init; }
    public long Devolvido { get; init; }
    public long Liquido { get; init; }
    public int Pedidos { get; init; }
    public int Fichas { get; init; }
    public long TicketMedio { get; init; }
    public long Sangrias { get; init; }
    public long Suprimentos { get; init; }
    public int Devolucoes { get; init; }
    /// <summary>Dinheiro que deve estar nas gavetas dos caixas abertos (os fechados já foram conferidos).</summary>
    public long DinheiroNoCaixa { get; init; }
    /// <summary>Caixas somados (no "caixa aberto", 0 ou 1 por máquina).</summary>
    public int Caixas { get; init; }
    public int CaixasAbertos { get; init; }
    public string? AbertoEm { get; init; }
    public string? UltimaVendaEm { get; init; }
    public List<FormaVendida> Formas { get; init; } = [];
    public List<ProdutoVendidoPainel> Produtos { get; init; } = [];
    public List<HoraVendida> PorHora { get; init; } = [];

    public static readonly Bloco Vazio = new();
}

/// <summary>Uma máquina: o caixa aberto agora e o evento todo.</summary>
public sealed record EstadoMaquina(
    string Instancia, int Caixa, string NomeEvento, string Versao, string Agora, Bloco CaixaAberto, Bloco TodoEvento);

/// <summary>Uma máquina vista pelo painel: a última resposta dela e se ainda está respondendo.</summary>
public sealed record MaquinaNoPainel(
    string Endereco, bool EstaMaquina, bool Online, int SegundosSemResposta, EstadoMaquina Estado);

/// <summary>O evento todo: as máquinas e a soma delas.</summary>
public sealed record EstadoEvento(
    string NomeEvento, string Agora, int MaquinasOnline, int MaquinasTotal, Bloco CaixaAberto, Bloco TodoEvento,
    List<MaquinaNoPainel> Maquinas);
