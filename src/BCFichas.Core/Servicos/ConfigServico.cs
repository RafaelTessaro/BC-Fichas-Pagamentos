using System.Text.Json;
using System.Text.Json.Serialization;
using BCFichas.Core.Dados;

namespace BCFichas.Core.Servicos;

public sealed class ConfigServico
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Banco _banco;

    public ConfigServico(Banco banco)
    {
        _banco = banco;
        Atual = Carregar();
    }

    public Configuracao Atual { get; private set; }

    public event Action<Configuracao>? Alterada;

    public void Salvar(Configuracao configuracao)
    {
        configuracao.NumeroCaixa = Math.Clamp(configuracao.NumeroCaixa, 1, 99);
        configuracao.Colunas = Math.Clamp(configuracao.Colunas, 2, 6);
        configuracao.Linhas = Math.Clamp(configuracao.Linhas, 2, 6);
        configuracao.LarguraPapelMm = configuracao.LarguraPapelMm <= 58 ? 58 : 80;
        configuracao.Zoom = Math.Clamp(configuracao.Zoom, 60, 160);
        configuracao.Rodape = Configuracao.RodapeSemMensagemFixa(configuracao.Rodape);
        configuracao.AjusteHorizontal = Math.Clamp(configuracao.AjusteHorizontal, -Configuracao.AjusteMaximo,
            Configuracao.AjusteMaximo);
        configuracao.VersaoConfig = Configuracao.VersaoAtual;

        var json = JsonSerializer.Serialize(configuracao, Json);
        _banco.Executar(
            "INSERT INTO config (chave, valor) VALUES ('geral', $v) ON CONFLICT(chave) DO UPDATE SET valor = excluded.valor",
            ("$v", json));
        Atual = configuracao.Clonar();
        Alterada?.Invoke(Atual);
    }

    private Configuracao Carregar()
    {
        var json = _banco.Escalar<string>("SELECT valor FROM config WHERE chave = 'geral'");
        if (string.IsNullOrEmpty(json)) return new Configuracao { VersaoConfig = Configuracao.VersaoAtual };
        try
        {
            var config = JsonSerializer.Deserialize<Configuracao>(json, Json) ?? new Configuracao();
            config.Rodape = Configuracao.RodapeSemMensagemFixa(config.Rodape);
            config.Atualizar();
            return config;
        }
        catch (JsonException)
        {
            return new Configuracao { VersaoConfig = Configuracao.VersaoAtual };
        }
    }
}
