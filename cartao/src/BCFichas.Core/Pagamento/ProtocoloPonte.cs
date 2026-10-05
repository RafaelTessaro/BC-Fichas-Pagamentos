using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCFichas.Core.Pagamento;

/// <summary>
/// Uma mensagem entre o tablet e o app ponte da maquininha. Cada mensagem é uma linha de JSON (termina em "\n").
/// O mesmo formato está no app ponte (Android): cartao/ponte-android.
/// </summary>
/// <remarks>
/// Tablet → ponte: ola, cobrar, consultar, cancelar, ping.
/// Ponte → tablet: ola, andamento, resultado, desconhecida (consulta de uma cobrança que nunca chegou), indefinida
/// (a cobrança chegou, mas a maquininha não sabe dizer se foi paga: o operador confere nela), ocupada (já está
/// cobrando outra), erro, pong.
/// </remarks>
public sealed class MensagemPonte
{
    public string Tipo { get; set; } = "";
    public int? Versao { get; set; }

    /// <summary>Identificador da cobrança no BC Fichas (o mesmo em todas as tentativas do mesmo pedido).</summary>
    public string? Id { get; set; }

    /// <summary>Código da venda que vai para o PagBank: até 10 letras e números.</summary>
    public string? Referencia { get; set; }

    /// <summary>
    /// Valor em centavos. Vai no "cobrar" e no "consultar", e volta no "resultado": a maquininha e o tablet conferem
    /// que o resultado é mesmo da cobrança daquele valor.
    /// </summary>
    public long? Valor { get; set; }

    /// <summary>"debito", "credito" ou "pix".</summary>
    public string? Forma { get; set; }

    /// <summary>Imprimir a via do estabelecimento na maquininha.</summary>
    public bool? Comprovante { get; set; }

    public string? Texto { get; set; }
    public bool? Aprovado { get; set; }
    public bool? Cancelado { get; set; }
    public string? Mensagem { get; set; }
    public string? Autorizacao { get; set; }
    public string? Nsu { get; set; }
    public string? Bandeira { get; set; }

    /// <summary>Código da transação no PagBank (o que se usa para estornar pelo PagBank).</summary>
    public string? Codigo { get; set; }

    public int? Caixa { get; set; }
    public string? Programa { get; set; }
    public string? Maquininha { get; set; }
    public string? Serial { get; set; }

    /// <summary>A maquininha está ativada e pronta para cobrar.</summary>
    public bool? Pronta { get; set; }
}

public static class ProtocoloPonte
{
    public const int Versao = 1;

    /// <summary>
    /// Serviço Bluetooth do app ponte (RFCOMM). O app também atende no serviço de porta serial padrão (SPP), para
    /// quem preferir ligar por uma porta COM do Windows.
    /// </summary>
    public static readonly Guid ServicoBluetooth = new("b7c1f00d-5f1c-4d2e-9a3b-2f6c8e4a1b10");

    /// <summary>Linha maior que isso não é mensagem do app ponte (lixo na ligação): é descartada.</summary>
    public const int MaiorLinha = 64 * 1024;

    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Linha(MensagemPonte mensagem) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensagem, Opcoes) + "\n");

    /// <summary>Lê uma linha recebida; nulo se não for uma mensagem válida.</summary>
    public static MensagemPonte? Ler(string linha)
    {
        if (string.IsNullOrWhiteSpace(linha)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<MensagemPonte>(linha, Opcoes);
            return m is { Tipo.Length: > 0 } ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Forma(FormaPagamento forma) => forma switch
    {
        FormaPagamento.Debito => "debito",
        FormaPagamento.Credito => "credito",
        FormaPagamento.Pix => "pix",
        _ => throw new ErroDeNegocio("Dinheiro não passa pela maquininha."),
    };

    /// <summary>
    /// Código da venda para o PagBank (até 10 letras e números): "P0123" (os últimos 4 dígitos do número do pedido) e
    /// mais 5 letras e números tirados do identificador completo da cobrança. O número do pedido se repete (modo
    /// teste, vendas apagadas, outro caixa), o código não: o app da maquininha usa ele para saber se a última venda
    /// aprovada no PagBank é mesmo aquela cobrança.
    /// </summary>
    public static string Referencia(string idCobranca, long numeroPedido)
    {
        // FNV-1a (64 bits) do identificador, escrito em base 36
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(idCobranca))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        const string digitos = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        Span<char> sufixo = stackalloc char[5];
        for (var i = 0; i < sufixo.Length; i++)
        {
            sufixo[i] = digitos[(int)(h % 36)];
            h /= 36;
        }
        var numero = (numeroPedido % 10_000 + 10_000) % 10_000;
        return string.Create(CultureInfo.InvariantCulture, $"P{numero:0000}{new string(sufixo)}");
    }
}
