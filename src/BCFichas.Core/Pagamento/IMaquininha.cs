namespace BCFichas.Core.Pagamento;

/// <summary>Pedido de cobrança enviado para a maquininha.</summary>
/// <param name="Id">Identificador único (usado para consultar depois se a conexão cair).</param>
public sealed record Cobranca(string Id, long ValorCentavos, FormaPagamento Forma, string Descricao);

public sealed record ResultadoCobranca(bool Aprovado, string Mensagem, string? Autorizacao = null);

/// <summary>
/// Conexão com a maquininha de cartão. Hoje existe o simulador; o app ponte via Bluetooth
/// vai implementar esta mesma interface.
/// </summary>
public interface IMaquininha
{
    string Nome { get; }

    /// <summary>Envia o valor para a maquininha e espera o cliente pagar.</summary>
    Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar);

    /// <summary>Pergunta o resultado de uma cobrança já enviada. Nulo se a maquininha não souber dizer.</summary>
    Task<ResultadoCobranca?> ConsultarAsync(string id, CancellationToken cancelar);
}
