using System.Globalization;
using BCFichas.Core;
using BCFichas.Core.Dados;
using BCFichas.Core.Servicos;
using Microsoft.Data.Sqlite;

namespace BCFichas.Painel;

/// <summary>
/// Lê as vendas desta máquina no banco do caixa, só para consulta. Guarda a última leitura: enquanto o caixa não
/// grava nada (PRAGMA data_version não muda), responde sem tocar no banco.
/// </summary>
public sealed class Leitor : IDisposable
{
    private readonly string _arquivo;
    private readonly object _trava = new();
    private Banco? _banco;
    private SqliteConnection? _vigia;
    private long _versaoConfig = -1;
    private long _versaoLida = -1;
    private EstadoMaquina? _estado;
    private Configuracao _config = new();

    public Leitor(string pastaDados, string instancia)
    {
        _arquivo = Path.Combine(pastaDados, "bcfichas.db");
        Instancia = instancia;
    }

    /// <summary>Muda a cada vez que o programa abre (separa duas máquinas com o mesmo número de caixa).</summary>
    public string Instancia { get; }

    public static string Versao { get; } =
        typeof(Leitor).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Configurações do caixa (PIN, máquinas da rede) como estavam na última leitura.</summary>
    public Configuracao Config
    {
        get
        {
            lock (_trava)
            {
                // Só o config (o programa confere a cada 5 s se o painel continua ligado): as vendas não são
                // somadas de novo se ninguém está olhando.
                Atualizar(estado: false);
                return _config;
            }
        }
    }

    /// <summary>Estado desta máquina e a marca dele (muda quando o caixa grava qualquer coisa).</summary>
    public (EstadoMaquina Estado, string Marca) Ler()
    {
        lock (_trava)
        {
            Atualizar(estado: true);
            return (_estado!, $"{Instancia}-{_versaoLida}");
        }
    }

    private void Atualizar(bool estado)
    {
        try
        {
            _banco ??= Banco.SomenteLeitura(_arquivo);
            if (_vigia is null)
            {
                _vigia = _banco.Abrir();
            }
            var versao = Banco.Escalar<long>(_vigia, null, "PRAGMA data_version");
            if (versao != _versaoConfig)
            {
                _config = new ConfigServico(_banco).Atual;
                _versaoConfig = versao;
            }
            if (!estado || (versao == _versaoLida && _estado is not null)) return;
            _estado = LerEstado(_banco, _config);
            _versaoLida = versao;
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException)
        {
            // Caixa reabrindo o banco, arquivo ainda não criado: tenta de novo na próxima pergunta.
            _vigia?.Dispose();
            _vigia = null;
            _versaoLida = -1;
            _versaoConfig = -1;
            _estado ??= new EstadoMaquina(Instancia, _config.NumeroCaixa, _config.NomeEvento, Versao, Agora(),
                Bloco.Vazio, Bloco.Vazio);
            Registro.Erro("Ler o banco do caixa", e);
        }
    }

    private EstadoMaquina LerEstado(Banco banco, Configuracao config)
    {
        var caixa = new CaixaServico(banco);
        // Caixas de verdade (os do modo teste ficam de fora, como nos relatórios do cliente)
        var sessoes = caixa.Sessoes(new DateTime(2000, 1, 1), DateTime.Today.AddYears(10)).OrderBy(s => s.Id).ToList();
        var resumos = sessoes.Select(s => caixa.Resumo(s.Id)).ToList();
        var aberto = resumos.Where(r => r.Sessao.Aberta && r.Sessao.Caixa == config.NumeroCaixa).ToList();
        return new EstadoMaquina(Instancia, config.NumeroCaixa, config.NomeEvento, Versao, Agora(),
            Somar(banco, aberto), Somar(banco, resumos));
    }

    /// <summary>Soma os resumos de vários caixas desta máquina num bloco só.</summary>
    internal static Bloco Somar(Banco banco, IReadOnlyList<ResumoCaixa> resumos)
    {
        if (resumos.Count == 0) return Bloco.Vazio;
        var ids = resumos.Select(r => r.Sessao.Id).ToList();
        var pedidos = resumos.Sum(r => r.QuantidadePedidos);
        var vendido = resumos.Sum(r => r.TotalVendas);
        var abertos = resumos.Where(r => r.Sessao.Aberta).ToList();
        return new Bloco
        {
            Vendido = vendido,
            Devolvido = resumos.Sum(r => r.TotalDevolvido),
            Liquido = resumos.Sum(r => r.VendaLiquida),
            Pedidos = pedidos,
            Fichas = resumos.Sum(r => r.QuantidadeFichas),
            TicketMedio = ResumoCaixa.Media(vendido, pedidos),
            Sangrias = resumos.Sum(r => r.Sangrias),
            Suprimentos = resumos.Sum(r => r.Suprimentos),
            Devolucoes = resumos.Sum(r => r.QuantidadeDevolucoes),
            DinheiroNoCaixa = abertos.Sum(r => r.DinheiroEsperado),
            Caixas = resumos.Count,
            CaixasAbertos = abertos.Count,
            AbertoEm = abertos.Count == 0 ? null : Data(abertos.Min(r => r.Sessao.AbertaEm)),
            UltimaVendaEm = UltimaVenda(banco, ids),
            Formas = Enum.GetValues<FormaPagamento>().Select(f => new FormaVendida(Chave(f), Nomes.De(f),
                    resumos.Sum(r => r.Total(f)), resumos.Sum(r => r.PedidosPorForma.GetValueOrDefault(f)),
                    resumos.Sum(r => r.Devolvido(f))))
                .ToList(),
            Produtos = resumos.SelectMany(r => r.Produtos)
                .GroupBy(p => p.Nome)
                .Select(g => new ProdutoVendidoPainel(g.Key, g.Sum(p => p.Quantidade), g.Sum(p => p.TotalCentavos)))
                .OrderByDescending(p => p.Quantidade).ThenBy(p => p.Nome)
                .ToList(),
            PorHora = PorHora(banco, ids),
        };
    }

    private static string? UltimaVenda(Banco banco, List<long> sessoes) =>
        banco.Escalar<string>(
            $"SELECT MAX(criado_em) FROM pedidos WHERE status = {(int)StatusPedido.Pago} AND sessao_id IN ({Lista(sessoes)})")
            is { } texto ? texto.Replace(' ', 'T') : null;

    private static List<HoraVendida> PorHora(Banco banco, List<long> sessoes) =>
        banco.Consultar($"""
            SELECT substr(criado_em, 1, 13), COUNT(*), SUM(total) FROM pedidos
            WHERE status = {(int)StatusPedido.Pago} AND sessao_id IN ({Lista(sessoes)})
            GROUP BY 1 ORDER BY 1
            """, l => new HoraVendida(l.GetString(0), l.GetInt32(1), l.GetInt64(2)));

    // Ids vindos do próprio banco (números): seguro montar a lista no SQL.
    private static string Lista(List<long> ids) => string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    internal static string Chave(FormaPagamento forma) => forma switch
    {
        FormaPagamento.Dinheiro => "dinheiro",
        FormaPagamento.Debito => "debito",
        FormaPagamento.Credito => "credito",
        FormaPagamento.Pix => "pix",
        _ => forma.ToString().ToLowerInvariant(),
    };

    internal static string Data(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    internal static string Agora() => Data(DateTime.Now);

    public void Dispose()
    {
        lock (_trava)
        {
            _vigia?.Dispose();
            _vigia = null;
        }
    }
}
