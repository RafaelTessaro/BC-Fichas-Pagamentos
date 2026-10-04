using System.Text.Json;
using System.Text.Json.Serialization;
using BCFichas.Core.Dados;
using Microsoft.Data.Sqlite;

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

    /// <summary>
    /// As configurações gravadas não deu para ler (ex.: gravadas por uma versão mais nova): o programa usa as
    /// padrão, e nada que dependa delas (como a limpeza das imagens sem uso) apaga arquivos.
    /// </summary>
    public bool ComDefeito { get; private set; }

    public event Action<Configuracao>? Alterada;

    public void Salvar(Configuracao configuracao)
    {
        _banco.Executar(SqlGravar, ("$v", Preparar(configuracao)));
        Aplicar(configuracao);
    }

    /// <summary>
    /// Grava junto com outras mudanças, na transação delas (restaurar o backup, zerar a programação): se faltar
    /// energia no meio, não fica a programação nova com as configurações da antiga. Depois do commit, chame
    /// <see cref="Aplicar"/>.
    /// </summary>
    internal static void Gravar(SqliteConnection c, SqliteTransaction t, Configuracao configuracao) =>
        Banco.Executar(c, t, SqlGravar, ("$v", Preparar(configuracao)));

    /// <summary>Passa a usar a configuração já gravada e avisa quem depende dela.</summary>
    internal void Aplicar(Configuracao configuracao)
    {
        Atual = configuracao.Clonar();
        ComDefeito = false;
        Alterada?.Invoke(Atual);
    }

    private const string SqlGravar =
        "INSERT INTO config (chave, valor) VALUES ('geral', $v) ON CONFLICT(chave) DO UPDATE SET valor = excluded.valor";

    /// <summary>Acerta os limites e devolve o JSON a gravar.</summary>
    private static string Preparar(Configuracao configuracao)
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
        return JsonSerializer.Serialize(configuracao, Json);
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
            ComDefeito = true;
            return new Configuracao { VersaoConfig = Configuracao.VersaoAtual };
        }
    }
}
